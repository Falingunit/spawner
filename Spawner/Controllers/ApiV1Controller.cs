using Microsoft.AspNetCore.Mvc;
using Spawner.Realtime;
using Spawner.Services;
using Spawner.JavaManager;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace Spawner.Controllers;

[ApiController]
[Route("api/v1")]
	public sealed class ApiV1Controller : ControllerBase
	{
		private readonly InstanceManager _manager;
		private readonly ConsoleStore _console;
		private readonly StatePublisher _publisher;
		private readonly Settings _settings;

	// Optional for realtime + idempotency on property saves
	private readonly EventBus? _bus;
	private readonly IdempotencyStore? _idempotency;

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private static readonly HttpClient MojangHttp = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(5)
	};

	private static readonly object VersionsLock = new();
	private static DateTime VersionsFetchedUtc = DateTime.MinValue;
	private static MojangVersionManifest? VersionsCache = null;

		public ApiV1Controller(
			InstanceManager manager,
			ConsoleStore console,
			StatePublisher publisher,
			Microsoft.Extensions.Options.IOptions<Settings> settings,
			EventBus? bus = null,
			IdempotencyStore? idempotency = null)
		{
			_manager = manager;
			_console = console;
			_publisher = publisher;
			_settings = settings.Value;
			_bus = bus;
			_idempotency = idempotency;
		}

	// -------------------- Servers list --------------------

	[HttpGet("servers")]
	public IActionResult ListServers([FromQuery] string fields = "basic")
	{
		var servers = BuildServerDtos();

		var payload = new { servers, serverTime = DateTime.UtcNow.ToString("O") };
		var etag = ComputeEtag(payload);

		if (Request.Headers.IfNoneMatch == etag)
			return StatusCode(304);

		Response.Headers.ETag = etag;
		return Ok(payload);
	}

		// -------------------- Create server (vanilla only for now) --------------------

	public sealed record CreateServerReq(
		[property: JsonPropertyName("name")] string? Name,
		[property: JsonPropertyName("type")] string? Type,
		[property: JsonPropertyName("version")] string? Version
	);

		[HttpPost("servers")]
		public async Task<IActionResult> CreateServer([FromBody] CreateServerReq body, CancellationToken ct)
		{
		var name = (body?.Name ?? "").Trim();
		if (name.Length == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "Name is required" } });

		var typeRaw = (body?.Type ?? "vanilla").Trim().ToLowerInvariant();
		if (!Enum.TryParse<Spawner.InstanceType>(typeRaw, ignoreCase: true, out var instanceType))
			return BadRequest(new { error = new { code = "bad_request", message = "Unknown instance type" } });

		if (instanceType != Spawner.InstanceType.Vanilla)
			return StatusCode(501, new { error = new { code = "not_implemented", message = "Only vanilla instance creation is implemented right now" } });

		var version = (body?.Version ?? "").Trim();
		if (version.Length == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "Version is required" } });

		var manifest = await GetMojangVersionManifest(ct);
		if (manifest.Versions.All(v => !string.Equals(v.Id, version, StringComparison.OrdinalIgnoreCase)))
			return BadRequest(new { error = new { code = "bad_request", message = "Unknown Minecraft version" } });

		var id = Guid.NewGuid().ToString("N");
		var instanceDir = Path.Combine(_manager.InstancesLocation, id);
		var props = new Spawner.InstanceProperties
		{
			InstanceID = id,
			InstanceName = name,
			InstanceType = instanceType,
			GameVersion = version,
			IsInitialized = false,
			InstanceDirectory = instanceDir,
			ServerJarName = "server.jar",
		};

		_manager.CreateInstance(props);
		_publisher.HookInstance(_manager.GetInstance(id));

		// Ensure the instance can be opened immediately (even while downloading).
			Directory.CreateDirectory(instanceDir);
			var eulaPath = Path.Combine(instanceDir, "eula.txt");
			if (!System.IO.File.Exists(eulaPath))
				System.IO.File.WriteAllText(eulaPath, "eula=true\n", Encoding.UTF8);
			// Do not auto-create server.properties; the server will generate it on first run.

		// Begin downloading/initializing in the background. Progress is published as server.patch(init=...).
		var queued = new Spawner.InstanceInitStatus(state: "downloading", stage: "server", fileName: null, bytesReceived: 0, totalBytes: null, percent: null, message: "Queued");
		_manager.SetInitStatus(id, queued);
		_bus?.Publish(Topics.Servers, new { kind = "server.patch", serverId = id, patch = new { init = queued, status = "downloading" } });

		// Publish a refreshed snapshot so connected clients see the new server immediately.
		_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });

		_manager.BeginInitializeInstanceInBackground(id, st =>
		{
			var nextStatus = st.state == "downloading" ? "downloading" : "offline";
			_bus?.Publish(Topics.Servers, new { kind = "server.patch", serverId = id, patch = new { init = st, status = nextStatus } });

			// When initialization fails, remove the half-created instance to keep the list clean.
			if (st.state == "error")
			{
				_manager.DeleteInstance(id, deleteFiles: true);
				_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });
			}
		});

		var created = _manager.GetInstanceProperties(id);
		var dto = BuildServerDto(created);
			return StatusCode(202, new { server = dto, serverTime = DateTime.UtcNow.ToString("O") });
		}

		// -------------------- Import server (custom) --------------------

		public sealed record ImportServerFromPathReq(
			[property: JsonPropertyName("name")] string? Name,
			[property: JsonPropertyName("version")] string? Version,
			[property: JsonPropertyName("serverJarPath")] string? ServerJarPath
		);

		public sealed record DetectVersionFromJarReq([property: JsonPropertyName("serverJarPath")] string? ServerJarPath);

		[HttpPost("minecraft:detect-version")]
		public IActionResult DetectMinecraftVersionFromJar([FromBody] DetectVersionFromJarReq body)
		{
			var serverJarPath = (body?.ServerJarPath ?? "").Trim();
			if (serverJarPath.Length == 0)
				return BadRequest(new { error = new { code = "bad_request", message = "serverJarPath is required" } });

			string jarFull;
			try { jarFull = Path.GetFullPath(serverJarPath); }
			catch { return BadRequest(new { error = new { code = "bad_request", message = "Invalid serverJarPath" } }); }

			if (!System.IO.File.Exists(jarFull))
				return NotFound(new { error = new { code = "not_found", message = "server.jar not found at that path" } });

			if (TryDetectVersionFromJarPath(jarFull, out var detected, out var candidates))
			{
				return Ok(new { detected = true, version = detected, serverTime = DateTime.UtcNow.ToString("O") });
			}

			return Ok(new
			{
				detected = false,
				candidates = candidates,
				serverTime = DateTime.UtcNow.ToString("O")
			});
		}

		[HttpPost("servers:import-path")]
		public async Task<IActionResult> ImportServerFromPath([FromBody] ImportServerFromPathReq body, CancellationToken ct)
		{
			var name = (body?.Name ?? "").Trim();
			if (name.Length == 0)
				return BadRequest(new { error = new { code = "bad_request", message = "Name is required" } });

			var version = (body?.Version ?? "").Trim();

			// Validate against Mojang's version manifest (same list as vanilla creation).
			var manifest = await GetMojangVersionManifest(ct);

			if (version.Length == 0)
			{
				var jarProbe = (body?.ServerJarPath ?? "").Trim();
				try
				{
					var fullProbe = Path.GetFullPath(jarProbe);
					if (System.IO.File.Exists(fullProbe) && TryDetectVersionFromJarPath(fullProbe, out var detected, out _))
						version = detected;
				}
				catch
				{
					// ignore and fall back to requiring explicit version
				}

				if (version.Length == 0)
					return BadRequest(new { error = new { code = "version_required", message = "Version is required (unable to auto-detect)" } });
			}

			var verInfo = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, version, StringComparison.OrdinalIgnoreCase));
			if (verInfo is null)
				return BadRequest(new { error = new { code = "bad_request", message = "Unknown Minecraft version" } });

			var serverJarPath = (body?.ServerJarPath ?? "").Trim();
			if (serverJarPath.Length == 0)
				return BadRequest(new { error = new { code = "bad_request", message = "serverJarPath is required" } });

			string jarFull;
			try { jarFull = Path.GetFullPath(serverJarPath); }
			catch { return BadRequest(new { error = new { code = "bad_request", message = "Invalid serverJarPath" } }); }

			if (!System.IO.File.Exists(jarFull))
				return NotFound(new { error = new { code = "not_found", message = "server.jar not found at that path" } });

			var srcDir = Path.GetDirectoryName(jarFull);
			if (string.IsNullOrWhiteSpace(srcDir) || !Directory.Exists(srcDir))
				return BadRequest(new { error = new { code = "bad_request", message = "Invalid serverJarPath directory" } });

			var id = Guid.NewGuid().ToString("N");
			var instanceDir = Path.Combine(_manager.InstancesLocation, id);
			Directory.CreateDirectory(instanceDir);

			CopyDirectoryRecursive(srcDir, instanceDir);

			// Ensure required Java runtime is installed and set javaPath/default args.
			string javaPath;
			try
			{
				javaPath = await EnsureJavaForMinecraftVersionAsync(verInfo.Url, ct);
			}
			catch (Exception ex)
			{
				// Don't leave a half-copied instance around if we can't even determine/install Java.
				try { Directory.Delete(instanceDir, recursive: true); } catch { }
				return StatusCode(409, new { error = new { code = "java_install_failed", message = ex.Message } });
			}

			// Ensure baseline config files exist so the instance is editable and startable.
				var eulaPath = Path.Combine(instanceDir, "eula.txt");
				if (!System.IO.File.Exists(eulaPath))
					System.IO.File.WriteAllText(eulaPath, "eula=true\n", Encoding.UTF8);
				// Do not auto-create server.properties; the server will generate it on first run.

			var props = new Spawner.InstanceProperties
			{
				InstanceID = id,
				InstanceName = name,
				InstanceType = Spawner.InstanceType.Custom,
				GameVersion = version,
				IsInitialized = true,
				InstanceDirectory = instanceDir,
				ServerJarName = Path.GetFileName(jarFull),
				JavaArgs = _settings.DefaultJavaArgs ?? "",
				JavaPath = javaPath,
			};

			_manager.CreateInstance(props);
			_publisher.HookInstance(_manager.GetInstance(id));

			_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });

			var created = _manager.GetInstanceProperties(id);
			var dto = BuildServerDto(created);
			return StatusCode(201, new { server = dto, serverTime = DateTime.UtcNow.ToString("O") });
		}

		private static void CopyDirectoryRecursive(string srcDir, string dstDir)
		{
			Directory.CreateDirectory(dstDir);

			foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(file);
				System.IO.File.Copy(file, Path.Combine(dstDir, name), overwrite: true);
			}

			foreach (var sub in Directory.EnumerateDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(sub);
				CopyDirectoryRecursive(sub, Path.Combine(dstDir, name));
			}
		}

		private static async Task<string> EnsureJavaForMinecraftVersionAsync(string versionInfoUrl, CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(versionInfoUrl))
				throw new InvalidOperationException("Missing version info URL.");

			using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			var versionJson = await client.GetStringAsync(versionInfoUrl, ct);
			var node = JsonNode.Parse(versionJson);

			var majorNode = node?["javaVersion"]?["majorVersion"];
			var majorStr = majorNode?.ToString()?.Trim();
			if (string.IsNullOrWhiteSpace(majorStr))
				throw new InvalidOperationException("Failed to determine required Java version.");

			await JavaRuntimeManager.InstallNewJavaVersion(client, majorStr, onProgress: null, ct);

			var javaPath = JavaRuntimeManager.GetInstalledJavaVersions().GetValueOrDefault(majorStr);
			if (string.IsNullOrWhiteSpace(javaPath))
				throw new InvalidOperationException("Failed to locate installed Java runtime.");

			return javaPath;
		}

		private static bool TryDetectVersionFromJarPath(string jarFull, out string version, out List<string> candidates)
		{
			version = "";
			candidates = new List<string>();

			try
			{
				// If the jar is already under .../versions/<ver>/..., use that segment.
				var parts = jarFull.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
					.Where(p => !string.IsNullOrWhiteSpace(p))
					.ToArray();

				for (var i = 0; i + 1 < parts.Length; i++)
				{
					if (string.Equals(parts[i], "versions", StringComparison.OrdinalIgnoreCase))
					{
						var candidate = parts[i + 1];
						if (!string.IsNullOrWhiteSpace(candidate))
						{
							version = candidate;
							return true;
						}
					}
				}

				var dir = Path.GetDirectoryName(jarFull);
				if (string.IsNullOrWhiteSpace(dir)) return false;

				var versionsDir = Path.Combine(dir, "versions");
				if (!Directory.Exists(versionsDir)) return false;

				candidates = Directory.EnumerateDirectories(versionsDir, "*", SearchOption.TopDirectoryOnly)
					.Select(Path.GetFileName)
					.Where(x => !string.IsNullOrWhiteSpace(x))
					.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
					.ToList()!;

				if (candidates.Count == 1)
				{
					version = candidates[0];
					return true;
				}

				return false;
			}
			catch
			{
				return false;
			}
		}

	// -------------------- Metadata --------------------

		[HttpGet("instance-types")]
		public IActionResult ListInstanceTypes()
		{
			var values = Enum.GetValues<Spawner.InstanceType>();
			var types = values.Select(t => new
			{
				id = t.ToString().ToLowerInvariant(),
				label = t.ToString(),
				implemented = t == Spawner.InstanceType.Vanilla || t == Spawner.InstanceType.Custom
			});

			return Ok(new { types, serverTime = DateTime.UtcNow.ToString("O") });
		}

		// -------------------- Instance settings --------------------

		public sealed record SetInstanceTypeReq([property: JsonPropertyName("type")] string? Type);

		[HttpPost("servers/{serverId}:set-type")]
		public IActionResult SetInstanceType([FromRoute] string serverId, [FromBody] SetInstanceTypeReq body)
		{
			var typeRaw = (body?.Type ?? "").Trim();
			if (!Enum.TryParse<Spawner.InstanceType>(typeRaw, ignoreCase: true, out var instanceType))
				return BadRequest(new { error = new { code = "bad_request", message = "Unknown instance type" } });

			Spawner.InstanceProperties props;
			try { props = _manager.GetInstanceProperties(serverId); }
			catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

			props.InstanceType = instanceType;
			_manager.PersistInstanceProperties();

			_bus?.Publish(Topics.Servers, new { kind = "server.patch", serverId, patch = new { type = instanceType.ToString().ToLowerInvariant() } });
			_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });

			return Ok(new { type = instanceType.ToString().ToLowerInvariant(), serverTime = DateTime.UtcNow.ToString("O") });
		}

	public sealed record MojangVersionLatest(
		[property: JsonPropertyName("release")] string Release,
		[property: JsonPropertyName("snapshot")] string Snapshot
	);

	public sealed record MojangVersionInfo(
		[property: JsonPropertyName("id")] string Id,
		[property: JsonPropertyName("type")] string Type,
		[property: JsonPropertyName("url")] string Url,
		[property: JsonPropertyName("time")] DateTime Time,
		[property: JsonPropertyName("releaseTime")] DateTime ReleaseTime
	);

	public sealed record MojangVersionManifest(
		[property: JsonPropertyName("latest")] MojangVersionLatest Latest,
		[property: JsonPropertyName("versions")] List<MojangVersionInfo> Versions
	);

	[HttpGet("minecraft/versions")]
	public async Task<IActionResult> ListMinecraftVersions(CancellationToken ct)
	{
		var manifest = await GetMojangVersionManifest(ct);
		var versions = manifest.Versions
			.OrderByDescending(v => v.ReleaseTime)
			.Select(v => new
			{
				id = v.Id,
				type = v.Type,
				releaseTimeUtc = v.ReleaseTime.ToUniversalTime().ToString("O"),
			});

		return Ok(new
		{
			latest = new { release = manifest.Latest.Release, snapshot = manifest.Latest.Snapshot },
			versions,
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	// -------------------- Start / stop (idempotent) --------------------

	[HttpPost("servers/{serverId}:start")]
	public IActionResult StartServer([FromRoute] string serverId)
	{
		var idem = Request.Headers["Idempotency-Key"].ToString();
		if (string.IsNullOrWhiteSpace(idem))
			return BadRequest(new { error = new { code = "missing_idempotency_key", message = "Idempotency-Key required" } });

		if (!_manager.InstanceExists(serverId))
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		try
		{
			var props = _manager.GetInstanceProperties(serverId);
			if (props.IsArchived)
				return StatusCode(409, new { error = new { code = "archived", message = "Server is archived" } });
		}
		catch
		{
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });
		}

		var store = HttpContext.RequestServices.GetRequiredService<IdempotencyStore>();
		var key = $"start:{serverId}:{idem}";

		if (store.TryGet(key, out var cached))
			return StatusCode(202, cached);

		var job = new { id = $"job_{Guid.NewGuid():N}", type = "start", serverId, state = "queued" };
		var resp = new { job, server = new { id = serverId, status = "starting" } };

		store.Set(key, resp);
		try
		{
			_manager.StartInstance(serverId);
		}
		catch (Exception ex)
		{
			store.Remove(key);
			return StatusCode(409, new { error = new { code = "start_failed", message = ex.Message } });
		}

		return StatusCode(202, resp);
	}

	[HttpPost("servers/{serverId}:stop")]
	public async Task<IActionResult> StopServer([FromRoute] string serverId, [FromQuery] bool force = false)
	{
		var idem = Request.Headers["Idempotency-Key"].ToString();
		if (string.IsNullOrWhiteSpace(idem))
			return BadRequest(new { error = new { code = "missing_idempotency_key", message = "Idempotency-Key required" } });

		if (!_manager.InstanceExists(serverId))
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		var store = HttpContext.RequestServices.GetRequiredService<IdempotencyStore>();
		var key = $"stop:{serverId}:{(force ? "force" : "graceful")}:{idem}";

		if (store.TryGet(key, out var cached))
			return StatusCode(202, cached);

		var job = new { id = $"job_{Guid.NewGuid():N}", type = force ? "force_stop" : "stop", serverId, state = "queued" };
		var resp = new { job, server = new { id = serverId, status = "stopping" } };

		store.Set(key, resp);
		try
		{
			if (force) await _manager.ForceStopInstance(serverId);
			else await _manager.StopInstance(serverId);
		}
		catch (Exception ex)
		{
			store.Remove(key);
			return StatusCode(409, new { error = new { code = "stop_failed", message = ex.Message } });
		}

		return StatusCode(202, resp);
	}

	// -------------------- Archive / delete --------------------

	[HttpPost("servers/{serverId}:archive")]
	public IActionResult ArchiveServer([FromRoute] string serverId)
	{
		if (!_manager.InstanceExists(serverId))
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		if (_manager.GetInitStatus(serverId).state == "downloading")
			return StatusCode(409, new { error = new { code = "conflict", message = "Wait for download to finish before archiving" } });

		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "conflict", message = "Stop the server before archiving" } });

		var props = _manager.GetInstanceProperties(serverId);
		props.IsArchived = true;
		_manager.PersistInstanceProperties();

		_bus?.Publish(Topics.Servers, new { kind = "server.patch", serverId, patch = new { archived = true } });
		_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });

		return Ok(new { archived = true, serverTime = DateTime.UtcNow.ToString("O") });
	}

	[HttpPost("servers/{serverId}:unarchive")]
	public IActionResult UnarchiveServer([FromRoute] string serverId)
	{
		if (!_manager.InstanceExists(serverId))
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		var props = _manager.GetInstanceProperties(serverId);
		props.IsArchived = false;
		_manager.PersistInstanceProperties();

		_bus?.Publish(Topics.Servers, new { kind = "server.patch", serverId, patch = new { archived = false } });
		_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });

		return Ok(new { archived = false, serverTime = DateTime.UtcNow.ToString("O") });
	}

	[HttpDelete("servers/{serverId}")]
	public async Task<IActionResult> DeleteServer([FromRoute] string serverId)
	{
		if (!_manager.InstanceExists(serverId))
			return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		if (_manager.GetInitStatus(serverId).state == "downloading")
			return StatusCode(409, new { error = new { code = "conflict", message = "Wait for download to finish before deleting" } });

		// Don't delete running instances.
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "conflict", message = "Stop the server before deleting" } });

		// Best-effort: ensure any stray process is stopped.
		try { await _manager.ForceStopInstance(serverId); } catch { }

		var ok = _manager.DeleteInstance(serverId, deleteFiles: true);
		if (!ok) return NotFound(new { error = new { code = "not_found", message = "Server not found" } });

		_bus?.Publish(Topics.Servers, new { kind = "snapshot", servers = BuildServerDtos() });
		return Ok(new { deleted = true, serverTime = DateTime.UtcNow.ToString("O") });
	}

	// -------------------- Console --------------------

	[HttpGet("servers/{serverId}/console/history")]
	public IActionResult ConsoleHistory([FromRoute] string serverId, [FromQuery] int limit = 300)
	{
		limit = Math.Clamp(limit, 1, 2000);
		var lines = _console.Get(serverId, limit);
		return Ok(new { lines, serverTime = DateTime.UtcNow.ToString("O") });
	}

	// -------------------- Logs --------------------

	[HttpGet("servers/{serverId}/logs/tail")]
	public IActionResult LogsTail([FromRoute] string serverId, [FromQuery] int limit = 500)
	{
		limit = Math.Clamp(limit, 1, 5000);

		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		// Prefer actual `logs/latest.log`; fall back to in-memory console buffer.
		var latestLogPath = Path.Combine(instanceDir, "logs", "latest.log");
		if (System.IO.File.Exists(latestLogPath))
		{
			var lines = TailUtf8Lines(latestLogPath, limit, maxBytesFromEnd: 2 * 1024 * 1024);
			return Ok(new
			{
				source = "latest.log",
				lines,
				serverTime = DateTime.UtcNow.ToString("O")
			});
		}

		return Ok(new
		{
			source = "console",
			lines = _console.Get(serverId, limit),
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	[HttpGet("servers/{serverId}/logs/files")]
	public IActionResult LogsFiles([FromRoute] string serverId)
	{
		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		var logsDir = Path.Combine(instanceDir, "logs");
		if (!Directory.Exists(logsDir))
			return Ok(new { files = Array.Empty<object>(), serverTime = DateTime.UtcNow.ToString("O") });

		var files = Directory.GetFiles(logsDir)
			.Select(p => new FileInfo(p))
			.Where(fi => fi.Name.Equals("latest.log", StringComparison.OrdinalIgnoreCase) ||
						 fi.Name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) ||
						 fi.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(fi => fi.LastWriteTimeUtc)
			.Select(fi => new
			{
				name = fi.Name,
				size = fi.Length,
				lastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O"),
				compressed = fi.Extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
			})
			.ToList();

		return Ok(new { files, serverTime = DateTime.UtcNow.ToString("O") });
	}

	[HttpGet("servers/{serverId}/logs/file/{name}/tail")]
	public IActionResult LogsFileTail([FromRoute] string serverId, [FromRoute] string name, [FromQuery] int limit = 500)
	{
		limit = Math.Clamp(limit, 1, 10000);

		if (!TryValidateLogFileName(name, out var safeName))
			return BadRequest(new { error = new { code = "bad_request", message = "Invalid log file name" } });

		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		var logsDir = Path.Combine(instanceDir, "logs");
		var path = Path.Combine(logsDir, safeName);

		if (!System.IO.File.Exists(path))
			return NotFound(new { error = new { code = "not_found", message = "Log file not found" } });

		IReadOnlyList<string> lines = safeName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
			? TailGzipLines(path, limit, maxDecompressedBytes: 4 * 1024 * 1024)
			: TailUtf8Lines(path, limit, maxBytesFromEnd: 2 * 1024 * 1024);

		return Ok(new
		{
			name = safeName,
			lines,
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	// -------------------- Player resolve (name <-> uuid) --------------------

	[HttpGet("players/resolve")]
	public async Task<IActionResult> ResolvePlayer([FromQuery] string? name = null, [FromQuery] string? uuid = null)
	{
		name = (name ?? "").Trim();
		uuid = (uuid ?? "").Trim();

		if (name.Length == 0 && uuid.Length == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "name or uuid required" } });

		try
		{
			if (name.Length > 0)
			{
				var url = $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}";
				using var res = await MojangHttp.GetAsync(url);
				if (!res.IsSuccessStatusCode)
					return NotFound(new { error = new { code = "not_found", message = "Player not found" } });

				var json = await res.Content.ReadAsStringAsync();
				var doc = JsonDocument.Parse(json);
				var id = doc.RootElement.GetProperty("id").GetString() ?? "";
				var resolvedName = doc.RootElement.GetProperty("name").GetString() ?? name;
				if (!TryDashedUuid(id, out var dashed))
					return StatusCode(502, new { error = new { code = "bad_gateway", message = "Invalid Mojang response" } });

				return Ok(new { name = resolvedName, uuid = dashed });
			}

			var uuidNoDashes = NormalizeUuid(uuid);
			if (uuidNoDashes.Length != 32 || !uuidNoDashes.All(Uri.IsHexDigit))
				return BadRequest(new { error = new { code = "bad_request", message = "Invalid uuid" } });

			var profileUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{uuidNoDashes}";
			using var profileRes = await MojangHttp.GetAsync(profileUrl);
			if (!profileRes.IsSuccessStatusCode)
				return NotFound(new { error = new { code = "not_found", message = "Player not found" } });

			var profileJson = await profileRes.Content.ReadAsStringAsync();
			var profileDoc = JsonDocument.Parse(profileJson);
			var id2 = profileDoc.RootElement.GetProperty("id").GetString() ?? "";
			var resolvedName2 = profileDoc.RootElement.GetProperty("name").GetString() ?? "";
			if (!TryDashedUuid(id2, out var dashed2))
				return StatusCode(502, new { error = new { code = "bad_gateway", message = "Invalid Mojang response" } });

			return Ok(new { name = resolvedName2, uuid = dashed2 });
		}
		catch (TaskCanceledException)
		{
			return StatusCode(504, new { error = new { code = "timeout", message = "Mojang request timed out" } });
		}
		catch
		{
			return StatusCode(502, new { error = new { code = "bad_gateway", message = "Failed to resolve player" } });
		}
	}

	public record ConsoleCommandReq(string command, string requestId);

	[HttpPost("servers/{serverId}/console/commands")]
	public IActionResult SendConsoleCommand([FromRoute] string serverId, [FromBody] ConsoleCommandReq req)
	{
		if (string.IsNullOrWhiteSpace(req.command) || string.IsNullOrWhiteSpace(req.requestId))
			return BadRequest(new { error = new { code = "bad_request", message = "command and requestId required" } });

		try
		{
			_manager.SendCommandToInstance(serverId, req.command);
			return StatusCode(202, new { accepted = true, requestId = req.requestId });
		}
		catch (Exception ex)
		{
			return StatusCode(409, new { error = new { code = "command_rejected", message = ex.Message } });
		}
	}

	// -------------------- Properties (GET + PUT with revision + optional idempotency + optional WS publish) --------------------

	[HttpGet("servers/{serverId}/properties")]
	public IActionResult GetProperties([FromRoute] string serverId)
	{
		// Authoritative source: server.properties on disk (matches SaveProperties revision scheme).
		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		var serverPropsPath = Path.Combine(instanceDir, "server.properties");
		if (!System.IO.File.Exists(serverPropsPath))
			return NotFound(new { error = new { code = "not_found", message = "server.properties not found" } });

		var current = ReadServerPropertiesFile(serverPropsPath);
		var revision = ComputeRevision(current);

		return Ok(new { properties = current, revision, serverTime = DateTime.UtcNow.ToString("O") });
	}

	// -------------------- Whitelist (GET + PUT with revision) --------------------

	[HttpGet("servers/{serverId}/whitelist")]
	public IActionResult GetWhitelist([FromRoute] string serverId)
	{
		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		var path = Path.Combine(instanceDir, "whitelist.json");
		var entries = ReadWhitelistFile(path);
		var revision = ComputeWhitelistRevision(entries);

		return Ok(new { entries, revision, serverTime = DateTime.UtcNow.ToString("O") });
	}

	public sealed record WhitelistReq(List<WhitelistEntry> entries);

	[HttpPut("servers/{serverId}/whitelist")]
	public IActionResult SaveWhitelist([FromRoute] string serverId, [FromBody] WhitelistReq body)
	{
		if (body?.entries is null)
			return BadRequest(new { error = new { code = "bad_request", message = "Missing entries body" } });

		var ifMatch = Request.Headers["If-Match"].ToString();
		if (string.IsNullOrWhiteSpace(ifMatch))
			return StatusCode(428, new { error = new { code = "precondition_required", message = "If-Match required" } });

		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		Directory.CreateDirectory(instanceDir);

		var path = Path.Combine(instanceDir, "whitelist.json");
		var current = ReadWhitelistFile(path);
		var currentRevision = ComputeWhitelistRevision(current);

		if (!string.Equals(ifMatch, currentRevision, StringComparison.Ordinal))
		{
			return Conflict(new
			{
				error = new { code = "revision_conflict", message = "Revision mismatch. Reload before saving." },
				revision = currentRevision,
				entries = current
			});
		}

		var next = NormalizeWhitelist(body.entries);
		WriteWhitelistFileAtomic(path, next);

		var saved = ReadWhitelistFile(path);
		var nextRevision = ComputeWhitelistRevision(saved);

		var response = new { entries = saved, revision = nextRevision };

		if (_bus is not null)
		{
			_bus.Publish(Topics.Whitelist(serverId), new
			{
				kind = "whitelist.updated",
				serverId,
				revision = nextRevision,
				entries = saved
			});
		}

		return Ok(response);
	}

	// -------------------- Launch settings (GET + PUT with revision) --------------------

	[HttpGet("servers/{serverId}/launch-settings")]
	public IActionResult GetLaunchSettings([FromRoute] string serverId)
	{
		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var payload = new
		{
			javaPath = instProps.JavaPath ?? "",
			javaArgs = instProps.JavaArgs ?? "",
			serverJarName = instProps.ServerJarName ?? "server.jar"
		};

		var revision = ComputeLaunchRevision(payload.javaPath, payload.javaArgs, payload.serverJarName);
		return Ok(new { settings = payload, revision, serverTime = DateTime.UtcNow.ToString("O") });
	}

	public sealed record LaunchSettingsReq(string javaPath, string javaArgs, string serverJarName);

	[HttpPut("servers/{serverId}/launch-settings")]
	public IActionResult SaveLaunchSettings([FromRoute] string serverId, [FromBody] LaunchSettingsReq body)
	{
		if (body is null)
			return BadRequest(new { error = new { code = "bad_request", message = "Missing settings body" } });

		var ifMatch = Request.Headers["If-Match"].ToString();
		if (string.IsNullOrWhiteSpace(ifMatch))
			return StatusCode(428, new { error = new { code = "precondition_required", message = "If-Match required" } });

		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		if (_manager.IsInstanceRunning(serverId))
		{
			return StatusCode(409, new
			{
				error = new { code = "server_running", message = "Stop the server before changing launch settings." }
			});
		}

		var currentRevision = ComputeLaunchRevision(instProps.JavaPath ?? "", instProps.JavaArgs ?? "", instProps.ServerJarName ?? "server.jar");
		if (!string.Equals(ifMatch, currentRevision, StringComparison.Ordinal))
		{
			return Conflict(new
			{
				error = new { code = "revision_conflict", message = "Revision mismatch. Reload before saving." },
				revision = currentRevision,
				settings = new
				{
					javaPath = instProps.JavaPath ?? "",
					javaArgs = instProps.JavaArgs ?? "",
					serverJarName = instProps.ServerJarName ?? "server.jar"
				}
			});
		}

		var javaPath = (body.javaPath ?? "").Trim();
		var javaArgs = body.javaArgs ?? "";
		var serverJarName = (body.serverJarName ?? "").Trim();

		if (string.IsNullOrWhiteSpace(javaPath))
			return BadRequest(new { error = new { code = "bad_request", message = "javaPath required" } });
		if (string.IsNullOrWhiteSpace(serverJarName))
			return BadRequest(new { error = new { code = "bad_request", message = "serverJarName required" } });

		// Apply (affects next start)
		instProps.JavaPath = javaPath;
		instProps.JavaArgs = javaArgs;
		instProps.ServerJarName = serverJarName;
		_manager.PersistInstanceProperties();

		var nextRevision = ComputeLaunchRevision(javaPath, javaArgs, serverJarName);
		var response = new { settings = new { javaPath, javaArgs, serverJarName }, revision = nextRevision };

		if (_bus is not null)
		{
			_bus.Publish(Topics.Server(serverId), new
			{
				kind = "launch.updated",
				serverId,
				revision = nextRevision,
				settings = new { javaPath, javaArgs, serverJarName }
			});
		}

		return Ok(response);
	}

	[HttpPut("servers/{serverId}/properties")]
	public IActionResult SaveProperties([FromRoute] string serverId, [FromBody] Dictionary<string, object> properties)
	{
		// 0) Validate body
		if (properties is null)
			return BadRequest(new { error = new { code = "bad_request", message = "Missing properties body" } });

		// Spec: If-Match required
		var ifMatch = Request.Headers["If-Match"].ToString();
		if (string.IsNullOrWhiteSpace(ifMatch))
			return StatusCode(428, new { error = new { code = "precondition_required", message = "If-Match required" } });

		// Optional: Idempotency-Key for safe retries (works only if store available)
		var idem = Request.Headers["Idempotency-Key"].ToString();
		if (!string.IsNullOrWhiteSpace(idem))
		{
			var cachedKey = $"props:{serverId}:{idem}";
			var store = _idempotency ?? HttpContext.RequestServices.GetService<IdempotencyStore>();
			if (store is not null && store.TryGet(cachedKey, out var cachedObj))
				return Ok(cachedObj);
		}

		// 1) Load authoritative properties from disk (server.properties)
		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch { return NotFound(new { error = new { code = "not_found", message = "Server not found" } }); }

		var instanceDir = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(instanceDir))
			return NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });

		var serverPropsPath = Path.Combine(instanceDir, "server.properties");
		if (!System.IO.File.Exists(serverPropsPath))
			return NotFound(new { error = new { code = "not_found", message = "server.properties not found" } });

		var current = ReadServerPropertiesFile(serverPropsPath);
		var currentRevision = ComputeRevision(current);

		// 2) Conflict check
		if (!string.Equals(ifMatch, currentRevision, StringComparison.Ordinal))
		{
			return Conflict(new
			{
				error = new { code = "revision_conflict", message = "Revision mismatch. Reload before saving." },
				revision = currentRevision,
				properties = current
			});
		}

		// 3) Apply updates (merge)
		var next = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

		foreach (var kv in properties)
		{
			var key = kv.Key?.Trim();
			if (string.IsNullOrWhiteSpace(key))
				continue;

			var valueStr = CoerceToServerPropertyString(kv.Value);

			// Chosen behavior: null removes the key
			if (valueStr is null)
				next.Remove(key);
			else
				next[key] = valueStr;
		}

		// 4) Write atomically + reload
		WriteServerPropertiesFileAtomic(serverPropsPath, next);

		var saved = ReadServerPropertiesFile(serverPropsPath);
		var nextRevision = ComputeRevision(saved);

		var response = new
		{
			properties = saved,
			revision = nextRevision
		};

		// 5) Cache idempotent response (if requested)
		if (!string.IsNullOrWhiteSpace(idem))
		{
			var cachedKey = $"props:{serverId}:{idem}";
			var store = _idempotency ?? HttpContext.RequestServices.GetService<IdempotencyStore>();
			store?.Set(cachedKey, response);
		}

		// 6) Optional realtime notifications
		if (_bus is not null)
		{
			_bus.Publish($"server:{serverId}:properties", new
			{
				kind = "properties.updated",
				serverId,
				revision = nextRevision,
				patch = ComputePatch(current, saved)
			});

			// Optional dashboard patch
			var serverPatch = new Dictionary<string, object>();

			if (saved.TryGetValue("motd", out var motd)) serverPatch["motd"] = motd;
			if (saved.TryGetValue("server-port", out var portStr) && int.TryParse(portStr, out var port))
				serverPatch["port"] = port;
			if (saved.TryGetValue("max-players", out var maxStr) && int.TryParse(maxStr, out var max))
				serverPatch["playersMax"] = max;

			if (serverPatch.Count > 0)
			{
				_bus.Publish("servers", new
				{
					kind = "server.patch",
					serverId,
					patch = serverPatch
				});
			}
		}

		return Ok(response);
	}

	// -------------------- Server icon --------------------

	[HttpGet("servers/{serverId}/icon")]
	public IActionResult GetIcon([FromRoute] string serverId)
	{
		try
		{
			var instProps = _manager.GetInstanceProperties(serverId);
			var instanceDir = instProps.InstanceDirectory;
			if (string.IsNullOrWhiteSpace(instanceDir)) return NotFound();

			var iconPath = Path.Combine(instanceDir, "server-icon.png");
			if (!System.IO.File.Exists(iconPath)) return NotFound();

			return PhysicalFile(iconPath, "image/png");
		}
		catch
		{
			return NotFound();
		}
	}

	// -------------------- DTO / ETag helpers --------------------

	private List<object> BuildServerDtos()
	{
		return _manager.GetAllInstanceProperties()
			.Select(BuildServerDto)
			.ToList<object>();
	}

	private object BuildServerDto(Spawner.InstanceProperties p)
	{
		var init = _manager.GetInitStatus(p.InstanceID);
		var status = _manager.GetInstanceStatus(p.InstanceID);
		var props = _manager.GetInstanceServerProperties(p.InstanceID);

		var statusStr = MapStatus(status, init);

		return new
		{
			id = p.InstanceID,
			name = p.InstanceName,
			iconUrl = $"/api/v1/servers/{p.InstanceID}/icon",
			version = p.GameVersion,
			type = p.InstanceType.ToString().ToLowerInvariant(),
			status = statusStr,
			playersOnline = _manager.GetInstancePlayerCount(p.InstanceID),
			playersMax = props?.MaxPlayers ?? 0,
			port = props?.ServerPort ?? 0,
			motd = props?.Motd ?? "",
			archived = p.IsArchived,
			init
		};
	}

	private static string MapStatus(Spawner.InstanceStatus status, Spawner.InstanceInitStatus init)
	{
		var s = status.ToString().ToLowerInvariant();
		if (s != "offline") return s;
		if (init.state == "downloading") return "downloading";
		return "offline";
	}

	private static string ComputeEtag(object obj)
	{
		var json = JsonSerializer.Serialize(obj, JsonOpts);
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
		return $"\"{Convert.ToHexString(bytes)}\"";
	}

	// -------------------- server.properties helpers --------------------

	// Reads Mojang server.properties format: key=value, ignores comments/blank lines.
	private static Dictionary<string, string> ReadServerPropertiesFile(string path)
	{
		var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var rawLine in System.IO.File.ReadAllLines(path))
		{
			var line = rawLine.Trim();
			if (line.Length == 0) continue;
			if (line.StartsWith("#")) continue;

			var idx = line.IndexOf('=');
			if (idx <= 0) continue;

			var key = line[..idx].Trim();
			var value = line[(idx + 1)..].Trim();

			if (key.Length == 0) continue;
			dict[key] = value;
		}

		return dict;
	}

	// Writes stable ordering for stable revisions.
	private static void WriteServerPropertiesFileAtomic(string path, Dictionary<string, string> props)
	{
		var tmp = path + ".tmp";

		var lines = props
			.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
			.Select(kv => $"{kv.Key}={kv.Value}")
			.ToArray();

		System.IO.File.WriteAllLines(tmp, lines, Encoding.UTF8);

		// Atomic-ish replace: delete then move (works cross-platform but can momentarily remove the file).
		// If you need stronger guarantees, use OS-specific atomic replace semantics.
		if (System.IO.File.Exists(path))
			System.IO.File.Delete(path);

		System.IO.File.Move(tmp, path);
	}

	private static string ComputeRevision(Dictionary<string, string> props)
	{
		var canonical = string.Join("\n",
			props.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
				 .Select(kv => $"{kv.Key}={kv.Value}")
		);

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return "rev_" + Convert.ToHexString(bytes);
	}

	private static Dictionary<string, object> ComputePatch(
		Dictionary<string, string> before,
		Dictionary<string, string> after)
	{
		var patch = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

		foreach (var kv in after)
		{
			if (!before.TryGetValue(kv.Key, out var prev) || !string.Equals(prev, kv.Value, StringComparison.Ordinal))
				patch[kv.Key] = kv.Value;
		}

		foreach (var kv in before)
		{
			if (!after.ContainsKey(kv.Key))
				patch[kv.Key] = null!;
		}

		return patch;
	}

	private static string? CoerceToServerPropertyString(object? value)
	{
		if (value is null) return null;

		if (value is JsonElement je)
		{
			return je.ValueKind switch
			{
				JsonValueKind.String => je.GetString(),
				JsonValueKind.Number => je.GetRawText(),
				JsonValueKind.True => "true",
				JsonValueKind.False => "false",
				JsonValueKind.Null => null,
				_ => je.GetRawText()
			};
		}

		if (value is bool b) return b ? "true" : "false";
		if (value is int or long or float or double or decimal)
			return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

		return value.ToString();
	}

	// -------------------- logs helpers --------------------

	private static IReadOnlyList<string> TailUtf8Lines(string path, int limit, int maxBytesFromEnd)
	{
		try
		{
			var fi = new FileInfo(path);
			if (!fi.Exists) return Array.Empty<string>();

			var bytesToRead = (int)Math.Min(fi.Length, maxBytesFromEnd);
			if (bytesToRead <= 0) return Array.Empty<string>();

			var buffer = new byte[bytesToRead];
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			fs.Seek(-bytesToRead, SeekOrigin.End);
			var read = fs.Read(buffer, 0, bytesToRead);

			var text = Encoding.UTF8.GetString(buffer, 0, read);
			var lines = text.Replace("\r\n", "\n").Split('\n');
			return lines.Where(l => l.Length > 0).TakeLast(limit).ToArray();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static IReadOnlyList<string> TailGzipLines(string path, int limit, int maxDecompressedBytes)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
			using var reader = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

			var q = new Queue<string>(capacity: Math.Min(limit, 2048));
			var totalChars = 0;

			while (true)
			{
				var line = reader.ReadLine();
				if (line is null) break;

				totalChars += line.Length + 1;
				if (totalChars > maxDecompressedBytes) break;

				q.Enqueue(line);
				while (q.Count > limit) q.Dequeue();
			}

			return q.ToArray();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static bool TryValidateLogFileName(string input, out string safeName)
	{
		safeName = "";
		if (string.IsNullOrWhiteSpace(input)) return false;

		var name = input.Trim();
		if (name.Contains('/') || name.Contains('\\')) return false;
		if (name.Contains("..", StringComparison.Ordinal)) return false;

		safeName = Path.GetFileName(name);
		return safeName.Length > 0;
	}

	private static string NormalizeUuid(string uuid) => uuid.Replace("-", "").Trim().ToLowerInvariant();

	private static bool TryDashedUuid(string mojangId, out string dashed)
	{
		dashed = "";
		var s = (mojangId ?? "").Trim();
		if (s.Length != 32) return false;

		dashed = $"{s[..8]}-{s[8..12]}-{s[12..16]}-{s[16..20]}-{s[20..]}".ToLowerInvariant();
		return true;
	}

	// -------------------- whitelist helpers --------------------

	public sealed record WhitelistEntry(
		[property: JsonPropertyName("uuid")] string? Uuid,
		[property: JsonPropertyName("name")] string? Name
	);

	private static List<WhitelistEntry> ReadWhitelistFile(string path)
	{
		try
		{
			if (!System.IO.File.Exists(path)) return new List<WhitelistEntry>();
			var json = System.IO.File.ReadAllText(path);
			return JsonSerializer.Deserialize<List<WhitelistEntry>>(json) ?? new List<WhitelistEntry>();
		}
		catch
		{
			return new List<WhitelistEntry>();
		}
	}

	private static void WriteWhitelistFileAtomic(string path, List<WhitelistEntry> entries)
	{
		var tmp = path + ".tmp";
		var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
		System.IO.File.WriteAllText(tmp, json, Encoding.UTF8);
		System.IO.File.Move(tmp, path, overwrite: true);
	}

	private static string ComputeWhitelistRevision(List<WhitelistEntry> entries)
	{
		var canonical = string.Join("\n",
			entries
				.OrderBy(e => e.Uuid ?? "", StringComparer.OrdinalIgnoreCase)
				.ThenBy(e => e.Name ?? "", StringComparer.OrdinalIgnoreCase)
				.Select(e => $"{(e.Uuid ?? "").Trim()}|{(e.Name ?? "").Trim()}")
		);

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return "rev_" + Convert.ToHexString(bytes);
	}

	private static List<WhitelistEntry> NormalizeWhitelist(IEnumerable<WhitelistEntry> entries)
	{
		var list = new List<WhitelistEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var e in entries ?? Array.Empty<WhitelistEntry>())
		{
			var name = (e.Name ?? "").Trim();
			var uuid = (e.Uuid ?? "").Trim();
			if (name.Length == 0 && uuid.Length == 0) continue;

			var key = (uuid.Length > 0 ? "u:" + uuid : "n:" + name);
			if (!seen.Add(key)) continue;

			list.Add(new WhitelistEntry(uuid.Length > 0 ? uuid : null, name.Length > 0 ? name : null));
		}

		return list
			.OrderBy(e => e.Name ?? "", StringComparer.OrdinalIgnoreCase)
			.ThenBy(e => e.Uuid ?? "", StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static string ComputeLaunchRevision(string javaPath, string javaArgs, string serverJarName)
	{
		var canonical = string.Join("\n", new[]
		{
			"javaPath=" + (javaPath ?? "").Trim(),
			"javaArgs=" + (javaArgs ?? ""),
			"serverJarName=" + (serverJarName ?? "").Trim()
		});

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return "rev_" + Convert.ToHexString(bytes);
	}

	// -------------------- Mojang versions cache --------------------

	private static async Task<MojangVersionManifest> GetMojangVersionManifest(CancellationToken ct)
	{
		lock (VersionsLock)
		{
			if (VersionsCache != null && (DateTime.UtcNow - VersionsFetchedUtc) < TimeSpan.FromMinutes(10))
				return VersionsCache;
		}

		using var res = await MojangHttp.GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
		if (!res.IsSuccessStatusCode)
			throw new InvalidOperationException("Failed to fetch Minecraft versions from Mojang.");

		await using var stream = await res.Content.ReadAsStreamAsync(ct);
		var manifest = await JsonSerializer.DeserializeAsync<MojangVersionManifest>(stream, JsonOpts, ct);
		if (manifest == null || manifest.Versions == null || manifest.Latest == null)
			throw new InvalidOperationException("Invalid Mojang response.");

		lock (VersionsLock)
		{
			VersionsCache = manifest;
			VersionsFetchedUtc = DateTime.UtcNow;
		}

		return manifest;
	}
}
