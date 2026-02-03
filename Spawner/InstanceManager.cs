using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace Spawner
{
	public sealed record InstanceInitStatus(
		string state,
		string? stage,
		string? fileName,
		long bytesReceived,
		long? totalBytes,
		int? percent,
		string? message
	);

	public class InstanceManager
	{
		private readonly Settings _settings;
		private readonly string _instancesLocation;
		private readonly string[] _legacyCandidates;
		private readonly ConcurrentDictionary<string, Instance> _instances = new();
		private readonly ConcurrentDictionary<string, InstanceInitStatus> _initById = new();
		private readonly ConcurrentDictionary<string, Task> _initTasksById = new();

		public InstanceManager(IOptions<Settings> settings, IConfiguration configuration, IHostEnvironment env)
		{
			_settings = settings.Value;
			_instancesLocation = GetDefaultInstancesLocation();

			var legacyFromConfig = (configuration["Settings:InstancesLocation"] ?? "").Trim();
			var legacyDefault = Path.Combine(env.ContentRootPath, "Instances");

			_legacyCandidates = new[] { legacyFromConfig, legacyDefault }
				.Where(p => !string.IsNullOrWhiteSpace(p))
				.Select(p =>
				{
					try { return Path.GetFullPath(p); } catch { return p; }
				})
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public string InstancesLocation => _instancesLocation;

		public static string GetDefaultInstancesLocation()
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrWhiteSpace(appData))
				appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrWhiteSpace(appData))
				appData = AppContext.BaseDirectory;

			var root = Path.Combine(appData, "Spawner", "Instances");
			Directory.CreateDirectory(root);
			return root;
		}

		public void Initialize()
		{
			Directory.CreateDirectory(_instancesLocation);
			TryMigrateLegacyInstances();

			var instancePropertiesList = LoadInstanceProperties(_instancesLocation);

			foreach (var instanceProperties in instancePropertiesList)
			{
				// Instances always live under the appdata instances root now.
				instanceProperties.InstanceDirectory = Path.Combine(_instancesLocation, instanceProperties.InstanceID);
				var instance = new Instance(instanceProperties, _settings);
				_instances[instanceProperties.InstanceID] = instance;
			}

			SaveInstanceProperties(_instancesLocation);
		}

		public bool InstanceExists(string instanceID) =>
			!string.IsNullOrWhiteSpace(instanceID) && _instances.ContainsKey(instanceID);

		public async Task DownloadInstance(string instanceID)
		{
			var instance = GetInstanceOrThrow(instanceID);
			await instance.InitializeInstance();
			SaveInstanceProperties(_instancesLocation);
		}

		public InstanceInitStatus GetInitStatus(string instanceID)
		{
			if (string.IsNullOrWhiteSpace(instanceID))
				return new InstanceInitStatus(state: "idle", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: null, message: null);

			if (_initById.TryGetValue(instanceID, out var st))
				return st;

			try
			{
				var inst = GetInstanceOrThrow(instanceID);
				if (inst.InstanceProperties.IsInitialized)
					return new InstanceInitStatus(state: "ready", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: 100, message: null);
			}
			catch
			{
				// ignore
			}

			return new InstanceInitStatus(state: "idle", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: null, message: null);
		}

		public void SetInitStatus(string instanceID, InstanceInitStatus status)
		{
			if (string.IsNullOrWhiteSpace(instanceID)) return;
			_initById[instanceID] = status;
		}

		public void ClearInitStatus(string instanceID)
		{
			if (string.IsNullOrWhiteSpace(instanceID)) return;
			_initById.TryRemove(instanceID, out _);
		}

		public void BeginInitializeInstanceInBackground(string instanceID, Action<InstanceInitStatus> onUpdate)
		{
			if (string.IsNullOrWhiteSpace(instanceID)) return;

			var task = _initTasksById.GetOrAdd(instanceID, _key =>
			{
				return Task.Run(async () =>
				{
					try
					{
						var inst = GetInstanceOrThrow(instanceID);
						if (inst.InstanceProperties.IsInitialized)
						{
							var ready = new InstanceInitStatus(state: "ready", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: 100, message: null);
							_initById[instanceID] = ready;
							onUpdate(ready);
							return;
						}

						var start = new InstanceInitStatus(state: "downloading", stage: "server", fileName: null, bytesReceived: 0, totalBytes: null, percent: null, message: "Preparing download");
						_initById[instanceID] = start;
						onUpdate(start);

						await inst.InitializeInstance(
							onProgress: p =>
							{
								int? percent = p.TotalBytes is { } t && t > 0 ? (int?)Math.Clamp((p.BytesReceived * 100L) / t, 0, 100) : null;
								var st = new InstanceInitStatus(
									state: "downloading",
									stage: "server",
									fileName: p.FileName,
									bytesReceived: p.BytesReceived,
									totalBytes: p.TotalBytes,
									percent: percent,
									message: "Downloading server"
								);
								_initById[instanceID] = st;
								onUpdate(st);
								return Task.CompletedTask;
							},
							onProgressJava: p =>
							{
								int? percent = p.TotalBytes is { } t && t > 0 ? (int?)Math.Clamp((p.BytesReceived * 100L) / t, 0, 100) : null;
								var st = new InstanceInitStatus(
									state: "downloading",
									stage: "java",
									fileName: p.FileName,
									bytesReceived: p.BytesReceived,
									totalBytes: p.TotalBytes,
									percent: percent,
									message: $"Downloading Java {p.JavaVersion}"
								);
								_initById[instanceID] = st;
								onUpdate(st);
								return Task.CompletedTask;
							}
						);

							SaveInstanceProperties(_instancesLocation);

						var done = new InstanceInitStatus(state: "ready", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: 100, message: null);
						_initById[instanceID] = done;
						onUpdate(done);
					}
					catch (Exception ex)
					{
						var err = new InstanceInitStatus(state: "error", stage: null, fileName: null, bytesReceived: 0, totalBytes: null, percent: null, message: ex.Message);
						_initById[instanceID] = err;
						onUpdate(err);
					}
					finally
					{
						_initTasksById.TryRemove(instanceID, out _);
					}
				});
			});

			// Avoid unobserved exceptions; callers get updates via callback.
			_ = task.ContinueWith(_ => { }, TaskScheduler.Default);
		}

		public void StartInstance(string instanceID)
		{
			var inst = GetInstanceOrThrow(instanceID);
			if (inst.InstanceProperties.IsArchived)
				throw new InvalidOperationException("Instance is archived.");
			inst.StartInstance();
		}

		public async Task StopInstance(string instanceID)
		{
			await GetInstanceOrThrow(instanceID).StopInstance();
		}

		public async Task ForceStopInstance(string instanceID)
		{
			await GetInstanceOrThrow(instanceID).ForceStopInstance();
		}

		public void CreateInstance(InstanceProperties instanceProperties)
		{
			var instance = new Instance(instanceProperties, _settings);
			_instances[instanceProperties.InstanceID] = instance;

			SaveInstanceProperties(_instancesLocation);
		}

		public Instance GetInstance(string instanceID) => GetInstanceOrThrow(instanceID);

		public bool DeleteInstance(string instanceID, bool deleteFiles = true)
		{
			if (string.IsNullOrWhiteSpace(instanceID)) return false;

			if (!_instances.TryRemove(instanceID, out var instance))
				return false;

				if (deleteFiles)
				{
					try
					{
						var dir = instance.InstanceProperties.InstanceDirectory;
						if (string.IsNullOrWhiteSpace(dir))
							dir = Path.Combine(_instancesLocation, instanceID);

						if (!string.IsNullOrWhiteSpace(dir))
						{
							var root = Path.GetFullPath(_instancesLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
							var full = Path.GetFullPath(dir);

							// Safety: never delete outside InstancesLocation.
							if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
								Directory.Delete(full, recursive: true);
						}
					}
					catch
					{
						// best-effort
					}
				}

				ClearInitStatus(instanceID);
				SaveInstanceProperties(_instancesLocation);
				return true;
			}

		public List<InstanceProperties> GetAllInstanceProperties()
		{
			return _instances.Values.Select(i => i.InstanceProperties).ToList();
		}

		public IEnumerable<Instance> GetAllInstances() => _instances.Values;

		public InstanceProperties GetInstanceProperties(string instanceID) => GetInstanceOrThrow(instanceID).InstanceProperties;

		public bool IsInstanceRunning(string instanceID)
		{
			var inst = GetInstanceOrThrow(instanceID);
			// Status can lag slightly; Server.IsRunning is the most direct indicator.
			return inst.Server.IsRunning || inst.Status != InstanceStatus.Offline;
		}

		public InstanceStatus GetInstanceStatus(string instanceID) => GetInstanceOrThrow(instanceID).Status;

		public int GetInstancePlayerCount(string instanceID)
		{
			var instance = GetInstanceOrThrow(instanceID);
			return instance.Status == InstanceStatus.Online ? instance.GetCurrentPlayerCount() : 0;
		}

		public ServerProperties? GetInstanceServerProperties(string instanceID)
		{
			return GetInstanceOrThrow(instanceID).GetServerProperties();
		}

		public void SetInstanceServerProperties(string instanceID, ServerProperties serverProperties)
		{
			GetInstanceOrThrow(instanceID).SetServerProperties(serverProperties);
		}

		public void SendCommandToInstance(string instanceID, string command)
		{
			GetInstanceOrThrow(instanceID).Server.SendCommand(command);
		}

		public List<InstanceProperties> LoadInstanceProperties(string instancesLocation)
		{
			if (string.IsNullOrWhiteSpace(instancesLocation))
				throw new InvalidOperationException("Instances location is not configured.");

			Directory.CreateDirectory(instancesLocation);
			var instancesPath = Path.Combine(instancesLocation, "instances.json");

			if (!File.Exists(instancesPath))
			{
				File.WriteAllText(instancesPath, "[]");
				return new List<InstanceProperties>();
			}

			try
			{
				string json = File.ReadAllText(instancesPath);
				return JsonConvert.DeserializeObject<List<InstanceProperties>>(json) ?? new List<InstanceProperties>();
			}
			catch (JsonException)
			{
				TryBackupCorruptJson(instancesPath);
				File.WriteAllText(instancesPath, "[]");
				return new List<InstanceProperties>();
			}
		}

		public void SaveInstanceProperties(string instancesLocation)
		{
			if (string.IsNullOrWhiteSpace(instancesLocation))
				throw new InvalidOperationException("Instances location is not configured.");

			Directory.CreateDirectory(instancesLocation);

			List<InstanceProperties> instanceProperties = GetAllInstanceProperties();
			string json = JsonConvert.SerializeObject(instanceProperties, Formatting.Indented);

			var instancesPath = Path.Combine(instancesLocation, "instances.json");
			var tmp = instancesPath + ".tmp";

			File.WriteAllText(tmp, json);
			File.Move(tmp, instancesPath, overwrite: true);
		}

		public void PersistInstanceProperties()
		{
			SaveInstanceProperties(_instancesLocation);
		}

			private void TryMigrateLegacyInstances()
			{
				try
				{
					var targetJson = Path.Combine(_instancesLocation, "instances.json");
					if (File.Exists(targetJson)) return;

					foreach (var legacy in _legacyCandidates)
					{
						if (string.IsNullOrWhiteSpace(legacy)) continue;
						var legacyJson = Path.Combine(legacy, "instances.json");
						if (!File.Exists(legacyJson)) continue;

						var list = LoadInstanceProperties(legacy);
						if (list.Count == 0) continue;

						foreach (var p in list)
						{
							var srcDir = p.InstanceDirectory;
							if (string.IsNullOrWhiteSpace(srcDir))
								srcDir = Path.Combine(legacy, p.InstanceID);

							var dstDir = Path.Combine(_instancesLocation, p.InstanceID);
							p.InstanceDirectory = dstDir;

							if (Directory.Exists(dstDir)) continue;
							if (!Directory.Exists(srcDir)) continue;

							CopyDirectoryRecursive(srcDir, dstDir);
						}

						WriteInstancePropertiesFile(_instancesLocation, list);
						return;
					}
				}
				catch
				{
					// best-effort migration
				}
			}

			private static void WriteInstancePropertiesFile(string instancesLocation, List<InstanceProperties> list)
			{
				Directory.CreateDirectory(instancesLocation);

				var instancesPath = Path.Combine(instancesLocation, "instances.json");
				var tmp = instancesPath + ".tmp";

				var json = JsonConvert.SerializeObject(list ?? new List<InstanceProperties>(), Formatting.Indented);
				File.WriteAllText(tmp, json);
				File.Move(tmp, instancesPath, overwrite: true);
			}

		private static void CopyDirectoryRecursive(string srcDir, string dstDir)
		{
			Directory.CreateDirectory(dstDir);

			foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(file);
				File.Copy(file, Path.Combine(dstDir, name), overwrite: true);
			}

			foreach (var sub in Directory.EnumerateDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(sub);
				CopyDirectoryRecursive(sub, Path.Combine(dstDir, name));
			}
		}

		private Instance GetInstanceOrThrow(string instanceID)
		{
			if (string.IsNullOrWhiteSpace(instanceID))
				throw new ArgumentException("Instance ID is required.", nameof(instanceID));

			if (_instances.TryGetValue(instanceID, out var instance))
				return instance;

			throw new KeyNotFoundException($"Instance '{instanceID}' not found.");
		}

		private static void TryBackupCorruptJson(string path)
		{
			try
			{
				if (!File.Exists(path)) return;

				var backup = path + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
				File.Copy(path, backup, overwrite: true);
			}
			catch
			{
				// best-effort backup
			}
		}
	}
}
