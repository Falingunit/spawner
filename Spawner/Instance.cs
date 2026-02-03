using Microsoft.Extensions.Options;
using Spawner.JavaManager;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text;

namespace Spawner
{
	public class Instance
	{
		public MinecraftServer Server;
		public InstanceProperties InstanceProperties;
		public InstanceStatus Status { get { return _status; } }

		public event Action<InstanceStatus>? OnStatusChanged;
		public event Action<int>? OnPlayersChanged;

		private Settings _settings;
		private InstanceStatus _status;

		private List<string> _onlinePlayers;

		public Instance(InstanceProperties instanceProperties, Settings settings)
		{
			Server = new(instanceProperties.InstanceName, instanceProperties.InstanceDirectory);
			InstanceProperties = instanceProperties;
			_settings = settings;
			_status = InstanceStatus.Offline;
			_onlinePlayers = new();

			Server.OnStopped += () => SetStatus(InstanceStatus.Offline);
			Server.OnStdOutLine += (line) =>
			{
				// Server ready
				if (ServerLogParser.DoneRegex().IsMatch(line))
				{
					SetStatus(InstanceStatus.Online);
				}

				// Player login
				{
					var m = ServerLogParser.LoginRegex().Match(line);
					if (m.Success)
					{
						var playerName = m.Groups["name"].Value;
						_onlinePlayers.Add(playerName);
						OnPlayersChanged?.Invoke(_onlinePlayers.Count);
						return;
					}
				}

				// Player logout
				{
					var m = ServerLogParser.LogoutRegex().Match(line);
					if (m.Success)
					{
						var playerName = m.Groups["name"].Value;
						_onlinePlayers.Remove(playerName);
						OnPlayersChanged?.Invoke(_onlinePlayers.Count);
						return;
					}
				}
			};
		}

			public async Task InitializeVanillaInstance(Func<InstanceDownloadProgress, Task>? onProgress = null, Func<JavaDownloadProgress, Task>? onProgressJava = null, CancellationToken ct = default)
			{
				if (string.IsNullOrWhiteSpace(InstanceProperties.GameVersion))
					throw new InvalidOperationException("Instance GameVersion is required.");
				if (string.IsNullOrWhiteSpace(InstanceProperties.InstanceID))
					throw new InvalidOperationException("Instance InstanceID is required.");

				// Large downloads (server jar, Java runtime) can legitimately take longer than HttpClient's
				// default timeout. Rely on the CancellationToken instead.
				using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

			var manifestJson = await client.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
			JsonNode? manifest = JsonNode.Parse(manifestJson);

			var versions = manifest?["versions"]?.AsArray() ?? throw new Exception("Failed to fetch Minecraft versions.");
			var match = versions.FirstOrDefault(v => v?["id"]?.ToString() == InstanceProperties.GameVersion);
			if (match is null)
				throw new InvalidOperationException($"Unknown Minecraft version '{InstanceProperties.GameVersion}'.");

			var versionUrl = match?["url"]?.ToString();
			if (string.IsNullOrWhiteSpace(versionUrl))
				throw new Exception("Missing version URL in manifest.");

			var versionInfoResult = await client.GetStringAsync(versionUrl, ct);
			JsonNode? versionInfo = JsonNode.Parse(versionInfoResult);

			var serverDownloadUrl = versionInfo?["downloads"]?["server"]?["url"]?.ToString();
			var serverJarSha1 = versionInfo?["downloads"]?["server"]?["sha1"]?.ToString();
			var serverJarSize = versionInfo?["downloads"]?["server"]?["size"]?.GetValue<long>();

			if (string.IsNullOrWhiteSpace(serverDownloadUrl))
				throw new Exception("Failed to get server download URL.");
			if (string.IsNullOrWhiteSpace(serverJarSha1))
				throw new Exception("Failed to get server jar SHA1.");
				if (serverJarSize is null || serverJarSize.Value <= 0)
					throw new Exception("Failed to get server jar size.");

				var instanceDir = InstanceProperties.InstanceDirectory;
				if (string.IsNullOrWhiteSpace(instanceDir))
					instanceDir = Path.Combine(InstanceManager.GetDefaultInstancesLocation(), InstanceProperties.InstanceID);

				var serverJarFileName = "server.jar";
				var destinationPath = Path.Combine(instanceDir, serverJarFileName);

				var progress = new Progress<(long received, long? total)>(p =>
				{
					var (received, total) = p;
				if (onProgress != null)
				{
					_ = onProgress(new InstanceDownloadProgress(
						InstanceProperties.InstanceID,
						serverJarFileName,
						received,
						total
					));
				}
			});

			await Download.DownloadFile(
				client,
				serverDownloadUrl,
				destinationPath,
				progress,
				serverJarSize.Value,
				(HashAlgorithmName.SHA1, serverJarSha1),
				ct);

			// Download java (if it doesn't already exist)
			string javaVersion = versionInfo?["javaVersion"]?["majorVersion"]?.ToString()
				?? throw new Exception("Failed to get java version.");

			await JavaRuntimeManager.InstallNewJavaVersion(client, javaVersion, onProgressJava, ct);

				var javaPath = JavaRuntimeManager.GetInstalledJavaVersions().GetValueOrDefault(javaVersion);
				if (string.IsNullOrWhiteSpace(javaPath))
					throw new Exception("Failed to get installed java path.");

				InstanceProperties.JavaPath = javaPath;
				InstanceProperties.InstanceDirectory = instanceDir;
				InstanceProperties.ServerJarName = serverJarFileName;
				InstanceProperties.JavaArgs = _settings.DefaultJavaArgs;
				InstanceProperties.IsInitialized = true;

			// Create baseline config files so the instance is ready to start and editable in the UI.
			Directory.CreateDirectory(InstanceProperties.InstanceDirectory);

			var eulaPath = Path.Combine(InstanceProperties.InstanceDirectory, "eula.txt");
			if (!File.Exists(eulaPath))
				File.WriteAllText(eulaPath, "eula=true\n", Encoding.UTF8);

				// Do not auto-create server.properties; the server will generate it on first run.

			}

		public void InitializeFabricInstance()
		{
			throw new NotImplementedException("Fabric instance initialization is not implemented yet.");
		}

			public async Task InitializeInstance(Func<InstanceDownloadProgress, Task>? onProgress = null, Func<JavaDownloadProgress, Task>? onProgressJava = null, CancellationToken ct = default)
			{
				if (InstanceProperties.IsInitialized) return;

				if (InstanceProperties.InstanceType == InstanceType.Vanilla) await InitializeVanillaInstance(onProgress, onProgressJava, ct);
				else if (InstanceProperties.InstanceType == InstanceType.Fabric) InitializeFabricInstance();
				else if (InstanceProperties.InstanceType == InstanceType.Custom) InstanceProperties.IsInitialized = true;
			}

		public ServerProperties? GetServerProperties()
		{
			return ServerPropertiesLoader.Load(InstanceProperties.InstanceDirectory);
		}

		public void SetServerProperties(ServerProperties serverProperties)
		{
			ServerPropertiesLoader.Save(InstanceProperties.InstanceDirectory, serverProperties);
		}

		public int GetCurrentPlayerCount()
		{
			return _onlinePlayers.Count;
		}


		public void StartInstance()
		{
			SetStatus(InstanceStatus.Starting);
			try
			{
				Server.Start(InstanceProperties.JavaPath, InstanceProperties.JavaArgs, CancellationToken.None, InstanceProperties.ServerJarName);
			}
			catch (Exception ex)
			{
				// Ensure UI doesn't get stuck "starting" if process launch fails (bad java path, missing jar, etc.)
				SetStatus(InstanceStatus.Offline);
				throw new InvalidOperationException($"Failed to start instance '{InstanceProperties.InstanceID}': {ex.Message}", ex);
			}
		}

		public async Task StopInstance()
		{
			SetStatus(InstanceStatus.Stopping);

			// If we aren't actually running (e.g. start failed), don't get stuck in "stopping".
			if (!Server.IsRunning)
			{
				SetStatus(InstanceStatus.Offline);
				return;
			}

			await Server.Stop();
		}

		public async Task ForceStopInstance()
		{
			SetStatus(InstanceStatus.Stopping);

			if (!Server.IsRunning)
			{
				SetStatus(InstanceStatus.Offline);
				return;
			}

			await Server.ForceStop();
			SetStatus(InstanceStatus.Offline);
		}

		public void SetStatus(InstanceStatus newStatus)
		{
			_status = newStatus;
			OnStatusChanged?.Invoke(newStatus);
		}
	}


	partial class ServerLogParser
	{
		[GeneratedRegex(@"Done \(\d+(\.\d+)?s\)! For help, type ""help""")]
		public static partial Regex DoneRegex();

		[GeneratedRegex(@"\]: (?<name>[^[]+)\[.*\] logged in with entity id\b")]
		public static partial Regex LoginRegex();

		[GeneratedRegex(@"\]: (?<name>[^ ]+) lost connection:")]
		public static partial Regex LogoutRegex();
	}


	public class InstanceProperties
	{
		public string InstanceName { get; set; } = "";
		public string InstanceID { get; set;} = "";

		public string InstanceDirectory {get; set;} = "";
		public string JavaPath {get; set;} = "";
		public string JavaArgs {get; set;} = "";
		public string ServerJarName {get; set;} = "";

		public bool IsInitialized { get; set; } = false;
		public bool IsArchived { get; set; } = false;

		public InstanceType InstanceType { get; set; } = InstanceType.Vanilla;
		public string GameVersion { get; set; } = "";
		public string? FabricLoaderVersion { get; set; } = null;
	}

		public enum InstanceType
		{
			Vanilla = 0,
			Fabric = 1,
			Custom = 2,
		}

	public enum InstanceStatus
	{
		Offline = 0,
		Online = 1,
		Starting = 2,
		Stopping = 3,
	}

	public class ServerProperties
	{
		/* ===================== ENUMS ===================== */

		public enum Difficulty
		{
			Peaceful,
			Easy,
			Normal,
			Hard
		}

		public enum GameMode
		{
			Survival,
			Creative,
			Adventure,
			Spectator
		}

		public enum LevelType
		{
			Normal,
			Flat,
			LargeBiomes,
			Amplified,
			SingleBiomeSurface
		}

		public enum RegionFileCompression
		{
			Deflate,
			Lz4,
			None
		}

		/* ===================== NETWORKING / SECURITY ===================== */

		public bool AcceptsTransfers { get; set; }
		public bool EnableQuery { get; set; }
		public bool EnableRcon { get; set; }
		public bool EnableStatus { get; set; }
		public bool OnlineMode { get; set; }
		public bool PreventProxyConnections { get; set; }
		public string ServerIp { get; set; } = string.Empty;
		public int ServerPort { get; set; }
		public int QueryPort { get; set; }
		public string RconPassword { get; set; } = string.Empty;
		public int RconPort { get; set; }
		public int NetworkCompressionThreshold { get; set; }
		public int RateLimit { get; set; }

		/* ===================== GAMEPLAY ===================== */

		public Difficulty DifficultySetting { get; set; }
		public GameMode Gamemode { get; set; }
		public bool Hardcore { get; set; }
		public bool ForceGamemode { get; set; }
		public bool AllowFlight { get; set; }
		public bool GenerateStructures { get; set; }
		public int SpawnProtection { get; set; }
		public int PlayerIdleTimeout { get; set; }
		public int MaxPlayers { get; set; }
		public bool WhiteList { get; set; }
		public bool EnforceWhitelist { get; set; }

		/* ===================== WORLD ===================== */

		public string LevelName { get; set; } = "world";
		public string LevelSeed { get; set; } = string.Empty;
		public LevelType LevelTypeSetting { get; set; }
		public string GeneratorSettings { get; set; } = "{}";
		public int MaxWorldSize { get; set; }
		public int ViewDistance { get; set; }
		public int SimulationDistance { get; set; }

		/* ===================== PERFORMANCE ===================== */

		public long MaxTickTime { get; set; }
		public int MaxChainedNeighborUpdates { get; set; }
		public int EntityBroadcastRangePercentage { get; set; }
		public bool SyncChunkWrites { get; set; }
		public bool UseNativeTransport { get; set; }
		public RegionFileCompression RegionFileCompressionSetting { get; set; }
		public int PauseWhenEmptySeconds { get; set; }

		/* ===================== CHAT / MODERATION ===================== */

		public bool EnforceSecureProfile { get; set; }
		public bool EnableCodeOfConduct { get; set; }
		public string TextFilteringConfig { get; set; } = string.Empty;
		public int TextFilteringVersion { get; set; }

		/* ===================== LOGGING / ADMIN ===================== */

		public bool LogIps { get; set; }
		public bool BroadcastConsoleToOps { get; set; }
		public bool BroadcastRconToOps { get; set; }
		public int OpPermissionLevel { get; set; }
		public int FunctionPermissionLevel { get; set; }
		public string Motd { get; set; } = "A Minecraft Server";
		public string BugReportLink { get; set; } = string.Empty;
		public bool HideOnlinePlayers { get; set; }

		/* ===================== RESOURCE PACK ===================== */

		public bool RequireResourcePack { get; set; }
		public string ResourcePack { get; set; } = string.Empty;
		public string ResourcePackId { get; set; } = string.Empty;
		public string ResourcePackPrompt { get; set; } = string.Empty;
		public string ResourcePackSha1 { get; set; } = string.Empty;
		public string InitialEnabledPacks { get; set; } = "vanilla";
		public string InitialDisabledPacks { get; set; } = string.Empty;

		/* ===================== MANAGEMENT SERVER ===================== */

		public bool ManagementServerEnabled { get; set; }
		public string ManagementServerHost { get; set; } = "localhost";
		public int ManagementServerPort { get; set; }
		public string ManagementServerSecret { get; set; } = string.Empty;
		public bool ManagementServerTlsEnabled { get; set; }
		public string ManagementServerTlsKeystore { get; set; } = string.Empty;
		public string ManagementServerTlsKeystorePassword { get; set; } = string.Empty;
		public int StatusHeartbeatInterval { get; set; }
	}

	public sealed record InstanceDownloadProgress(
		string InstanceId,
		string FileName,
		long BytesReceived,
		long? TotalBytes
	);
}
