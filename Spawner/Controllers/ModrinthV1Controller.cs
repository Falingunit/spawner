using Microsoft.AspNetCore.Mvc;
using Spawner.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spawner.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class ModrinthV1Controller : ControllerBase
{
	private readonly InstanceManager _manager;
	private readonly ModrinthClient _modrinth;
	private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	public ModrinthV1Controller(InstanceManager manager, ModrinthClient modrinth)
	{
		_manager = manager;
		_modrinth = modrinth;
	}

	[HttpGet("modrinth/search")]
	public async Task<IActionResult> Search(
		[FromQuery] string? query = null,
		[FromQuery] string? projectType = "mod",
		[FromQuery] string? category = null,
		[FromQuery] string? loader = null,
		[FromQuery] string? mcVersion = null,
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 20,
		CancellationToken ct = default)
	{
		try
		{
			var res = await _modrinth.SearchProjectsAsync(query, projectType, category, loader, mcVersion, offset, limit, ct);
			return Ok(new
			{
				totalHits = res.TotalHits ?? 0,
				offset = res.Offset ?? offset,
				limit = res.Limit ?? limit,
				hits = (res.Hits ?? new())
					.Select(h => new
					{
						projectId = h.ProjectId,
						slug = h.Slug ?? "",
						title = h.Title ?? "",
						author = h.Author ?? "",
						description = h.Description ?? "",
						projectType = h.ProjectType ?? "",
						iconUrl = h.IconUrl ?? "",
						categories = h.Categories ?? new List<string>(),
						versions = h.Versions ?? new List<string>(),
						clientSide = h.ClientSide ?? "",
						serverSide = h.ServerSide ?? "",
						downloads = h.Downloads ?? 0,
						dateModifiedUtc = h.DateModified?.UtcDateTime.ToString("O")
					}),
				serverTime = DateTime.UtcNow.ToString("O")
			});
		}
		catch (Exception ex)
		{
			return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } });
		}
	}

	[HttpGet("modrinth/projects/{idOrSlug}")]
	public async Task<IActionResult> GetProject([FromRoute] string idOrSlug, CancellationToken ct)
	{
		try
		{
			var p = await _modrinth.GetProjectAsync(idOrSlug, ct);
			return Ok(new
			{
				project = new
				{
					id = p.Id,
					slug = p.Slug ?? "",
					title = p.Title ?? "",
					description = p.Description ?? "",
					projectType = p.ProjectType ?? "",
					iconUrl = p.IconUrl ?? "",
					categories = p.Categories ?? new List<string>(),
					clientSide = p.ClientSide ?? "",
					serverSide = p.ServerSide ?? "",
					publishedUtc = p.Published?.UtcDateTime.ToString("O"),
					updatedUtc = p.Updated?.UtcDateTime.ToString("O")
				},
				serverTime = DateTime.UtcNow.ToString("O")
			});
		}
		catch (Exception ex)
		{
			return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } });
		}
	}

	[HttpGet("modrinth/projects/{idOrSlug}/versions")]
	public async Task<IActionResult> GetProjectVersions(
		[FromRoute] string idOrSlug,
		[FromQuery] string? loader = null,
		[FromQuery] string? mcVersion = null,
		[FromQuery] bool includeChangelog = false,
		CancellationToken ct = default)
	{
		try
		{
			var loaders = string.IsNullOrWhiteSpace(loader) ? null : new[] { loader.Trim().ToLowerInvariant() };
			var gameVersions = string.IsNullOrWhiteSpace(mcVersion) ? null : new[] { mcVersion.Trim() };
			var versions = await _modrinth.GetProjectVersionsAsync(idOrSlug, loaders, gameVersions, includeChangelog, ct);
			return Ok(new
			{
				versions = versions.Select(v => new
				{
					id = v.Id,
					projectId = v.ProjectId,
					name = v.Name ?? "",
					versionNumber = v.VersionNumber ?? "",
					versionType = v.VersionType ?? "",
					status = v.Status ?? "",
					featured = v.Featured ?? false,
					loaders = v.Loaders ?? new List<string>(),
					gameVersions = v.GameVersions ?? new List<string>(),
					files = (v.Files ?? new List<ModrinthVersionFile>()).Select(f => new
					{
						filename = f.Filename,
						url = f.Url,
						size = f.Size ?? 0,
						primary = f.Primary ?? false,
						fileType = f.FileType,
						hashes = f.Hashes ?? new Dictionary<string, string>()
					})
				}),
				serverTime = DateTime.UtcNow.ToString("O")
			});
		}
		catch (Exception ex)
		{
			return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } });
		}
	}

	public sealed record InstallModrinthVersionReq([property: JsonPropertyName("versionId")] string? VersionId);
	public sealed record ModFileActionReq([property: JsonPropertyName("fileName")] string? FileName);

	[HttpGet("servers/{serverId}/mods")]
	public async Task<IActionResult> ListMods([FromRoute] string serverId, CancellationToken ct)
	{
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;

		var modsDir = Path.Combine(props.InstanceDirectory, "mods");
		var disabledDir = Path.Combine(modsDir, ".disabled");
		var items = EnumerateModFiles(modsDir, true)
			.Concat(EnumerateModFiles(disabledDir, false))
			.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		string? modrinthError = null;
		try { await EnrichModListWithModrinthAsync(items, props, ct); }
		catch (Exception ex) { modrinthError = ex.Message; }

		return Ok(new { items = items.Select(ToApiModItem), modrinthError, serverTime = DateTime.UtcNow.ToString("O") });
	}

	public sealed record ContentInstallReq(
		[property: JsonPropertyName("kind")] string? Kind,
		[property: JsonPropertyName("versionId")] string? VersionId
	);

	public sealed record ContentRemoveReq(
		[property: JsonPropertyName("kind")] string? Kind,
		[property: JsonPropertyName("fileName")] string? FileName
	);

	[HttpGet("servers/{serverId}/content/{kind}")]
	public async Task<IActionResult> ListContent([FromRoute] string serverId, [FromRoute] string kind, CancellationToken ct)
	{
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;
		if (!TryGetContentKind(kind, out var contentKind))
			return BadRequest(new { error = new { code = "bad_request", message = "Unknown content kind" } });

		var dir = GetContentDirectory(props, contentKind);
		var items = EnumerateGenericContentFiles(dir, contentKind)
			.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		string? modrinthError = null;
		try { await EnrichGenericContentWithModrinthAsync(items, props, contentKind, ct); }
		catch (Exception ex) { modrinthError = ex.Message; }

		return Ok(new
		{
			items = items.Select(x => new
			{
				fileName = x.FileName,
				size = x.Size,
				lastWriteTimeUtc = x.LastWriteTimeUtc,
				isDirectory = x.IsDirectory,
				displayName = x.DisplayName,
				iconUrl = x.IconUrl,
				projectId = x.ProjectId,
				projectSlug = x.ProjectSlug,
				versionId = x.VersionId,
				versionNumber = x.VersionNumber,
				isManual = string.IsNullOrWhiteSpace(x.ProjectId)
			}),
			modrinthError,
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	[HttpPost("servers/{serverId}/content:install-modrinth-version")]
	public async Task<IActionResult> InstallContentVersion([FromRoute] string serverId, [FromBody] ContentInstallReq body, CancellationToken ct)
	{
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;
		if (!TryGetContentKind(body?.Kind, out var contentKind))
			return BadRequest(new { error = new { code = "bad_request", message = "Unknown content kind" } });
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before installing content." } });

		var versionId = (body?.VersionId ?? "").Trim();
		if (versionId.Length == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "versionId is required" } });

		ModrinthVersion version;
		try { version = await _modrinth.GetVersionAsync(versionId, ct); }
		catch (Exception ex) { return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } }); }

		if (!IsVersionCompatibleWithContent(version, props, contentKind))
			return BadRequest(new { error = new { code = "incompatible_version", message = "Version is not compatible with this server." } });

		var file = SelectInstallableContentFile(version.Files ?? new(), contentKind);
		if (file is null)
			return BadRequest(new { error = new { code = "no_installable_file", message = "No installable file found for that version." } });

		var targetDir = GetContentDirectory(props, contentKind);
		Directory.CreateDirectory(targetDir);
		var safeName = Path.GetFileName(file.Filename ?? "");
		if (!TryValidateSafeContentFileName(safeName, out safeName))
			return BadRequest(new { error = new { code = "bad_file_name", message = "Invalid content file name." } });
		var dst = Path.Combine(targetDir, safeName);
		if (System.IO.File.Exists(dst))
			return Conflict(new { error = new { code = "already_exists", message = "A file with that name already exists." } });

		var tmp = dst + ".download";
		try
		{
			if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
			await _modrinth.DownloadToFileAsync(file.Url, tmp, ct);
			await VerifyHashesAsync(tmp, file.Hashes ?? new Dictionary<string, string>(), ct);
			System.IO.File.Move(tmp, dst, overwrite: false);
		}
		catch (Exception ex)
		{
			try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
			return StatusCode(409, new { error = new { code = "install_failed", message = ex.Message } });
		}

		return Ok(new { installed = new { kind = contentKind.ToString().ToLowerInvariant(), fileName = safeName }, serverTime = DateTime.UtcNow.ToString("O") });
	}

	[HttpPost("servers/{serverId}/content:remove")]
	public IActionResult RemoveContent([FromRoute] string serverId, [FromBody] ContentRemoveReq body)
	{
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;
		if (!TryGetContentKind(body?.Kind, out var contentKind))
			return BadRequest(new { error = new { code = "bad_request", message = "Unknown content kind" } });
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before modifying content." } });

		var fileName = (body?.FileName ?? "").Trim();
		if (!TryValidateSafeContentFileName(fileName, out var safeName))
			return BadRequest(new { error = new { code = "bad_request", message = "Invalid file name" } });

		var path = Path.Combine(GetContentDirectory(props, contentKind), safeName);
		if (System.IO.File.Exists(path))
		{
			System.IO.File.Delete(path);
			return Ok(new { removed = true, fileName = safeName, serverTime = DateTime.UtcNow.ToString("O") });
		}
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
			return Ok(new { removed = true, fileName = safeName, serverTime = DateTime.UtcNow.ToString("O") });
		}
		return NotFound(new { error = new { code = "not_found", message = "Content file not found" } });
	}

	[HttpPost("servers/{serverId}/mods:install-modrinth-version")]
	public async Task<IActionResult> InstallModrinthVersion([FromRoute] string serverId, [FromBody] InstallModrinthVersionReq body, CancellationToken ct)
	{
		var versionId = (body?.VersionId ?? "").Trim();
		if (versionId.Length == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "versionId is required" } });

		if (!TryGetFabricInstanceProps(serverId, out var props, out var err))
			return err!;
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before installing mods." } });

		ModrinthVersion version;
		try { version = await _modrinth.GetVersionAsync(versionId, ct); }
		catch (Exception ex) { return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } }); }

		if (!IsVersionCompatibleWithFabricInstance(version, props))
			return BadRequest(new { error = new { code = "incompatible_version", message = "Version is not compatible with this Fabric instance." } });

		var file = SelectInstallableModFile(version.Files ?? new());
		if (file is null)
			return BadRequest(new { error = new { code = "no_installable_file", message = "No installable .jar file found for that version." } });

		var result = await InstallVersionFileIntoModsAsync(props, version, file, overwriteExisting: false, ct);
		if (!result.Success)
			return StatusCode(result.StatusCode, new { error = new { code = result.Code, message = result.Message } });

		return Ok(new { installed = result.Payload, serverTime = DateTime.UtcNow.ToString("O") });
	}

	[HttpPost("servers/{serverId}/mods:disable")]
	public IActionResult DisableMod([FromRoute] string serverId, [FromBody] ModFileActionReq body) => MoveMod(serverId, body?.FileName, true);

	[HttpPost("servers/{serverId}/mods:enable")]
	public IActionResult EnableMod([FromRoute] string serverId, [FromBody] ModFileActionReq body) => MoveMod(serverId, body?.FileName, false);

	[HttpPost("servers/{serverId}/mods:remove")]
	public IActionResult RemoveMod([FromRoute] string serverId, [FromBody] ModFileActionReq body)
	{
		var fileName = (body?.FileName ?? "").Trim();
		if (!TryValidateJarName(fileName, out var safeName))
			return BadRequest(new { error = new { code = "bad_request", message = "Invalid mod file name" } });
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before modifying mods." } });

		var p1 = Path.Combine(props.InstanceDirectory, "mods", safeName);
		var p2 = Path.Combine(props.InstanceDirectory, "mods", ".disabled", safeName);
		if (System.IO.File.Exists(p1)) { System.IO.File.Delete(p1); return Ok(new { removed = true, fileName = safeName, serverTime = DateTime.UtcNow.ToString("O") }); }
		if (System.IO.File.Exists(p2)) { System.IO.File.Delete(p2); return Ok(new { removed = true, fileName = safeName, serverTime = DateTime.UtcNow.ToString("O") }); }
		return NotFound(new { error = new { code = "not_found", message = "Mod file not found" } });
	}

	[HttpPost("servers/{serverId}/mods:update")]
	public async Task<IActionResult> UpdateMod([FromRoute] string serverId, [FromBody] ModFileActionReq body, CancellationToken ct)
	{
		var fileName = (body?.FileName ?? "").Trim();
		if (!TryValidateJarName(fileName, out var safeName))
			return BadRequest(new { error = new { code = "bad_request", message = "Invalid mod file name" } });
		if (!TryGetFabricInstanceProps(serverId, out var props, out var err))
			return err!;
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before updating mods." } });

		var (currentPath, enabled) = ResolveModPath(props, safeName);
		if (currentPath is null)
			return NotFound(new { error = new { code = "not_found", message = "Mod file not found" } });

		var sha1 = await ComputeHashHexAsync(currentPath, SHA1.Create(), ct);
		Dictionary<string, ModrinthVersion> updateMap;
		try
		{
			updateMap = await _modrinth.GetLatestVersionsFromHashesAsync(
				new[] { sha1 }, "sha1", new[] { "fabric" },
				string.IsNullOrWhiteSpace(props.GameVersion) ? Array.Empty<string>() : new[] { props.GameVersion },
				ct);
		}
		catch (Exception ex)
		{
			return StatusCode(502, new { error = new { code = "modrinth_error", message = ex.Message } });
		}

		if (!updateMap.TryGetValue(sha1, out var nextVersion))
			return Ok(new { updated = false, reason = "No Modrinth update found for this file.", serverTime = DateTime.UtcNow.ToString("O") });

		var nextFile = SelectInstallableModFile(nextVersion.Files ?? new());
		if (nextFile is null)
			return StatusCode(409, new { error = new { code = "no_installable_file", message = "No installable file found for updated version." } });

		var targetDir = enabled ? Path.Combine(props.InstanceDirectory, "mods") : Path.Combine(props.InstanceDirectory, "mods", ".disabled");
		Directory.CreateDirectory(targetDir);
		var rawNextName = Path.GetFileName(nextFile.Filename ?? "");
		if (!TryValidateJarName(rawNextName, out var nextName))
			return StatusCode(409, new { error = new { code = "bad_file_name", message = "Invalid updated file name." } });

		var dst = Path.Combine(targetDir, nextName);
		var tmp = dst + ".download";
		try
		{
			if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
			await _modrinth.DownloadToFileAsync(nextFile.Url, tmp, ct);
			await VerifyHashesAsync(tmp, nextFile.Hashes ?? new Dictionary<string, string>(), ct);
			if (!string.Equals(currentPath, dst, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(dst))
				return Conflict(new { error = new { code = "already_exists", message = "Updated file name already exists." } });
			System.IO.File.Move(tmp, dst, overwrite: true);
			if (!string.Equals(currentPath, dst, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(currentPath))
				System.IO.File.Delete(currentPath);
		}
		catch (Exception ex)
		{
			try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
			return StatusCode(409, new { error = new { code = "update_failed", message = ex.Message } });
		}

		return Ok(new
		{
			updated = true,
			fileName = nextName,
			enabled,
			versionId = nextVersion.Id,
			projectId = nextVersion.ProjectId,
			versionNumber = nextVersion.VersionNumber ?? "",
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	[HttpPost("servers/{serverId}/mods:import-mrpack")]
	[RequestSizeLimit(1024L * 1024L * 1024L)]
	public async Task<IActionResult> ImportMrpack([FromRoute] string serverId, [FromForm] IFormFile? file, CancellationToken ct)
	{
		if (file is null || file.Length <= 0)
			return BadRequest(new { error = new { code = "bad_request", message = "mrpack file is required" } });
		if (!TryGetFabricInstanceProps(serverId, out var props, out var err))
			return err!;
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before importing a modpack." } });

		var tmpPath = Path.Combine(Path.GetTempPath(), "spawner_mrpack_" + Guid.NewGuid().ToString("N") + ".mrpack");
		try
		{
			await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
				await file.CopyToAsync(fs, ct);
			var result = await ImportMrpackInternalAsync(props, tmpPath, ct);
			return Ok(new { imported = result, serverTime = DateTime.UtcNow.ToString("O") });
		}
		catch (Exception ex)
		{
			return StatusCode(409, new { error = new { code = "mrpack_import_failed", message = ex.Message } });
		}
		finally
		{
			try { if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath); } catch { }
		}
	}

	[HttpGet("servers/{serverId}/mods:export-mrpack")]
	public async Task<IActionResult> ExportMrpack([FromRoute] string serverId, CancellationToken ct)
	{
		if (!TryGetFabricInstanceProps(serverId, out var props, out var err))
			return err!;
		try
		{
			var bytes = await BuildMrpackExportAsync(props, ct);
			var safe = string.Join("-", (props.InstanceName ?? "instance").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
			if (string.IsNullOrWhiteSpace(safe)) safe = "instance";
			return File(bytes, "application/x-modrinth-modpack+zip", $"{safe}.mrpack");
		}
		catch (Exception ex)
		{
			return StatusCode(409, new { error = new { code = "mrpack_export_failed", message = ex.Message } });
		}
	}

	private IActionResult MoveMod(string serverId, string? fileName, bool toDisabled)
	{
		var raw = (fileName ?? "").Trim();
		if (!TryValidateJarName(raw, out var safeName))
			return BadRequest(new { error = new { code = "bad_request", message = "Invalid mod file name" } });
		if (!TryGetInstanceProps(serverId, out var props, out var notFound))
			return notFound!;
		if (_manager.IsInstanceRunning(serverId))
			return StatusCode(409, new { error = new { code = "server_running", message = "Stop the server before modifying mods." } });

		var modsDir = Path.Combine(props.InstanceDirectory, "mods");
		var disabledDir = Path.Combine(modsDir, ".disabled");
		Directory.CreateDirectory(modsDir);
		Directory.CreateDirectory(disabledDir);
		var src = toDisabled ? Path.Combine(modsDir, safeName) : Path.Combine(disabledDir, safeName);
		var dst = toDisabled ? Path.Combine(disabledDir, safeName) : Path.Combine(modsDir, safeName);
		if (!System.IO.File.Exists(src))
			return NotFound(new { error = new { code = "not_found", message = "Mod file not found" } });
		if (System.IO.File.Exists(dst))
			return Conflict(new { error = new { code = "already_exists", message = "A mod file with that name already exists in the target folder." } });

		System.IO.File.Move(src, dst);
		return Ok(new { fileName = safeName, enabled = !toDisabled, serverTime = DateTime.UtcNow.ToString("O") });
	}

	private sealed class InstalledModFileDto
	{
		public required string FileName { get; init; }
		public required string FullPath { get; init; }
		public bool Enabled { get; init; }
		public long Size { get; init; }
		public required string LastWriteTimeUtc { get; init; }
		public string? Sha1 { get; set; }
		public string DisplayName { get; set; } = "";
		public string? IconUrl { get; set; }
		public string? ProjectId { get; set; }
		public string? ProjectSlug { get; set; }
		public string? VersionId { get; set; }
		public string? VersionNumber { get; set; }
		public bool UpdateAvailable { get; set; }
		public string? UpdateVersionId { get; set; }
		public string? UpdateVersionNumber { get; set; }
		public bool IsManual => string.IsNullOrWhiteSpace(ProjectId);
	}

	private static object ToApiModItem(InstalledModFileDto x) => new
	{
		fileName = x.FileName,
		enabled = x.Enabled,
		size = x.Size,
		lastWriteTimeUtc = x.LastWriteTimeUtc,
		sha1 = x.Sha1,
		displayName = string.IsNullOrWhiteSpace(x.DisplayName) ? x.FileName : x.DisplayName,
		iconUrl = x.IconUrl,
		projectId = x.ProjectId,
		projectSlug = x.ProjectSlug,
		versionId = x.VersionId,
		versionNumber = x.VersionNumber,
		isManual = x.IsManual,
		update = new { available = x.UpdateAvailable, versionId = x.UpdateVersionId, versionNumber = x.UpdateVersionNumber }
	};

	private static IEnumerable<InstalledModFileDto> EnumerateModFiles(string dir, bool enabled)
	{
		if (!Directory.Exists(dir)) yield break;
		foreach (var path in Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly))
		{
			FileInfo fi;
			try { fi = new FileInfo(path); } catch { continue; }
			yield return new InstalledModFileDto
			{
				FileName = fi.Name,
				FullPath = fi.FullName,
				Enabled = enabled,
				Size = fi.Length,
				LastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O"),
				DisplayName = fi.Name
			};
		}
	}

	private async Task EnrichModListWithModrinthAsync(List<InstalledModFileDto> items, InstanceProperties props, CancellationToken ct)
	{
		if (items.Count == 0) return;
		foreach (var item in items) item.Sha1 = await ComputeHashHexAsync(item.FullPath, SHA1.Create(), ct);

		var hashes = items.Select(x => x.Sha1!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var byHash = await _modrinth.GetVersionsFromHashesAsync(hashes, "sha1", ct);

		var projectIds = byHash.Values.Select(v => v.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var projects = new Dictionary<string, ModrinthProject>(StringComparer.OrdinalIgnoreCase);
		foreach (var pid in projectIds) { try { projects[pid] = await _modrinth.GetProjectAsync(pid, ct); } catch { } }

		Dictionary<string, ModrinthVersion> updates = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!string.IsNullOrWhiteSpace(props.GameVersion))
				updates = await _modrinth.GetLatestVersionsFromHashesAsync(hashes, "sha1", new[] { "fabric" }, new[] { props.GameVersion }, ct);
		}
		catch { }

		foreach (var item in items)
		{
			if (item.Sha1 is null || !byHash.TryGetValue(item.Sha1, out var version)) continue;
			item.ProjectId = version.ProjectId;
			item.VersionId = version.Id;
			item.VersionNumber = version.VersionNumber ?? "";
			if (projects.TryGetValue(version.ProjectId, out var project))
			{
				item.ProjectSlug = project.Slug;
				item.DisplayName = project.Title ?? item.FileName;
				item.IconUrl = project.IconUrl;
			}
			if (updates.TryGetValue(item.Sha1, out var next) && !string.Equals(next.Id, version.Id, StringComparison.OrdinalIgnoreCase))
			{
				item.UpdateAvailable = true;
				item.UpdateVersionId = next.Id;
				item.UpdateVersionNumber = next.VersionNumber ?? "";
			}
		}
	}

	private enum ContentKind
	{
		ResourcePacks,
		DataPacks
	}

	private sealed class GenericContentItemDto
	{
		public required string FileName { get; init; }
		public required string FullPath { get; init; }
		public bool IsDirectory { get; init; }
		public long Size { get; init; }
		public required string LastWriteTimeUtc { get; init; }
		public string DisplayName { get; set; } = "";
		public string? ProjectId { get; set; }
		public string? ProjectSlug { get; set; }
		public string? VersionId { get; set; }
		public string? VersionNumber { get; set; }
		public string? IconUrl { get; set; }
		public string? Sha1 { get; set; }
	}

	private static bool TryGetContentKind(string? raw, out ContentKind kind)
	{
		var s = (raw ?? "").Trim().ToLowerInvariant();
		if (s is "resourcepack" or "resourcepacks")
		{
			kind = ContentKind.ResourcePacks;
			return true;
		}
		if (s is "datapack" or "datapacks")
		{
			kind = ContentKind.DataPacks;
			return true;
		}
		kind = default;
		return false;
	}

	private static string GetContentDirectory(InstanceProperties props, ContentKind kind) =>
		kind switch
		{
			ContentKind.ResourcePacks => Path.Combine(props.InstanceDirectory, "resourcepacks"),
			ContentKind.DataPacks => Path.Combine(props.InstanceDirectory, "world", "datapacks"),
			_ => props.InstanceDirectory
		};

	private static IEnumerable<GenericContentItemDto> EnumerateGenericContentFiles(string dir, ContentKind kind)
	{
		if (!Directory.Exists(dir)) yield break;

		foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
		{
			FileInfo fi;
			try { fi = new FileInfo(file); } catch { continue; }
			if (!IsValidGenericContentFileName(fi.Name, kind)) continue;
			yield return new GenericContentItemDto
			{
				FileName = fi.Name,
				FullPath = fi.FullName,
				IsDirectory = false,
				Size = fi.Length,
				LastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O"),
				DisplayName = fi.Name
			};
		}

		foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
		{
			DirectoryInfo di;
			try { di = new DirectoryInfo(sub); } catch { continue; }
			yield return new GenericContentItemDto
			{
				FileName = di.Name,
				FullPath = di.FullName,
				IsDirectory = true,
				Size = 0,
				LastWriteTimeUtc = di.LastWriteTimeUtc.ToString("O"),
				DisplayName = di.Name
			};
		}
	}

	private async Task EnrichGenericContentWithModrinthAsync(List<GenericContentItemDto> items, InstanceProperties props, ContentKind kind, CancellationToken ct)
	{
		var hashable = items.Where(x => !x.IsDirectory).ToList();
		if (hashable.Count == 0) return;
		foreach (var item in hashable) item.Sha1 = await ComputeHashHexAsync(item.FullPath, SHA1.Create(), ct);
		var hashes = hashable.Select(x => x.Sha1!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var byHash = await _modrinth.GetVersionsFromHashesAsync(hashes, "sha1", ct);
		var projectIds = byHash.Values.Select(v => v.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var projects = new Dictionary<string, ModrinthProject>(StringComparer.OrdinalIgnoreCase);
		foreach (var pid in projectIds) { try { projects[pid] = await _modrinth.GetProjectAsync(pid, ct); } catch { } }

		foreach (var item in hashable)
		{
			if (item.Sha1 is null || !byHash.TryGetValue(item.Sha1, out var version)) continue;
			if (!IsVersionCompatibleWithContent(version, props, kind)) continue;
			item.ProjectId = version.ProjectId;
			item.VersionId = version.Id;
			item.VersionNumber = version.VersionNumber ?? "";
			if (projects.TryGetValue(version.ProjectId, out var p))
			{
				item.ProjectSlug = p.Slug;
				item.DisplayName = p.Title ?? item.FileName;
				item.IconUrl = p.IconUrl;
			}
		}
	}

	private static bool IsVersionCompatibleWithContent(ModrinthVersion version, InstanceProperties props, ContentKind kind)
	{
		if (!string.IsNullOrWhiteSpace(props.GameVersion) && (version.GameVersions?.Count ?? 0) > 0 &&
			!(version.GameVersions ?? new()).Any(x => string.Equals(x, props.GameVersion, StringComparison.OrdinalIgnoreCase)))
			return false;

		if (kind == ContentKind.ResourcePacks || kind == ContentKind.DataPacks)
			return true;
		return true;
	}

	private static ModrinthVersionFile? SelectInstallableContentFile(IReadOnlyList<ModrinthVersionFile> files, ContentKind kind)
	{
		static bool MatchExt(string name, params string[] exts) => exts.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
		return kind switch
		{
			ContentKind.ResourcePacks => files.FirstOrDefault(f => (f.Primary ?? false) && MatchExt(f.Filename ?? "", ".zip"))
				?? files.FirstOrDefault(f => MatchExt(f.Filename ?? "", ".zip")),
			ContentKind.DataPacks => files.FirstOrDefault(f => (f.Primary ?? false) && MatchExt(f.Filename ?? "", ".zip"))
				?? files.FirstOrDefault(f => MatchExt(f.Filename ?? "", ".zip")),
			_ => null
		};
	}

	private static bool IsValidGenericContentFileName(string name, ContentKind kind)
	{
		var n = (name ?? "").Trim();
		if (n.Length == 0) return false;
		return kind switch
		{
			ContentKind.ResourcePacks => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
			ContentKind.DataPacks => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
			_ => false
		};
	}

	private sealed record InstallVersionFileResult(bool Success, int StatusCode, string Code, string Message, object? Payload);

	private async Task<InstallVersionFileResult> InstallVersionFileIntoModsAsync(InstanceProperties props, ModrinthVersion version, ModrinthVersionFile file, bool overwriteExisting, CancellationToken ct)
	{
		var safeName = Path.GetFileName(file.Filename ?? "");
		if (string.IsNullOrWhiteSpace(safeName) || !safeName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
			return new(false, 400, "bad_file_name", "Invalid mod file name.", null);

		List<string> removedFileNames;
		try
		{
			removedFileNames = await RemoveExistingProjectModFilesAsync(props, version.ProjectId, ct);
		}
		catch (Exception ex)
		{
			return new(false, 409, "install_failed", ex.Message, null);
		}

		var modsDir = Path.Combine(props.InstanceDirectory, "mods");
		Directory.CreateDirectory(modsDir);
		var dst = Path.Combine(modsDir, safeName);
		if (!overwriteExisting && System.IO.File.Exists(dst))
			return new(false, 409, "already_exists", "A mod file with that name already exists.", null);

		var tmp = dst + ".download";
		try
		{
			if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
			await _modrinth.DownloadToFileAsync(file.Url, tmp, ct);
			await VerifyHashesAsync(tmp, file.Hashes ?? new Dictionary<string, string>(), ct);
			System.IO.File.Move(tmp, dst, overwrite: overwriteExisting);
		}
		catch (Exception ex)
		{
			try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
			return new(false, 409, "install_failed", ex.Message, null);
		}
		return new(true, 200, "", "", new { fileName = safeName, enabled = true, versionId = version.Id, projectId = version.ProjectId, versionNumber = version.VersionNumber ?? "", removedFileNames });
	}

	private async Task<List<string>> RemoveExistingProjectModFilesAsync(InstanceProperties props, string projectId, CancellationToken ct)
	{
		var removed = new List<string>();
		if (string.IsNullOrWhiteSpace(projectId)) return removed;

		var candidates = EnumerateModFiles(Path.Combine(props.InstanceDirectory, "mods"), true)
			.Concat(EnumerateModFiles(Path.Combine(props.InstanceDirectory, "mods", ".disabled"), false))
			.ToList();
		if (candidates.Count == 0) return removed;

		foreach (var item in candidates)
			item.Sha1 = await ComputeHashHexAsync(item.FullPath, SHA1.Create(), ct);

		var hashes = candidates.Select(x => x.Sha1).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (hashes.Length == 0) return removed;

		Dictionary<string, ModrinthVersion> byHash;
		try { byHash = await _modrinth.GetVersionsFromHashesAsync(hashes, "sha1", ct); }
		catch { return removed; }

		foreach (var item in candidates)
		{
			if (string.IsNullOrWhiteSpace(item.Sha1)) continue;
			if (!byHash.TryGetValue(item.Sha1, out var v)) continue;
			if (!string.Equals(v.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) continue;
			try
			{
				if (System.IO.File.Exists(item.FullPath))
				{
					System.IO.File.Delete(item.FullPath);
					removed.Add(item.FileName);
				}
			}
			catch
			{
				// best-effort; install may still proceed and hit filename conflict
			}
		}

		return removed;
	}

	private static bool IsVersionCompatibleWithFabricInstance(ModrinthVersion version, InstanceProperties props)
	{
		if (!(version.Loaders ?? new()).Any(x => string.Equals(x, "fabric", StringComparison.OrdinalIgnoreCase))) return false;
		if (!string.IsNullOrWhiteSpace(props.GameVersion) && (version.GameVersions?.Count ?? 0) > 0 &&
			!(version.GameVersions ?? new()).Any(x => string.Equals(x, props.GameVersion, StringComparison.OrdinalIgnoreCase))) return false;
		return true;
	}

	private (string? Path, bool Enabled) ResolveModPath(InstanceProperties props, string safeName)
	{
		var p1 = Path.Combine(props.InstanceDirectory, "mods", safeName);
		if (System.IO.File.Exists(p1)) return (p1, true);
		var p2 = Path.Combine(props.InstanceDirectory, "mods", ".disabled", safeName);
		if (System.IO.File.Exists(p2)) return (p2, false);
		return (null, true);
	}

	private static ModrinthVersionFile? SelectInstallableModFile(IReadOnlyList<ModrinthVersionFile> files) =>
		files.FirstOrDefault(f => (f.Primary ?? false) && (f.Filename ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
		?? files.FirstOrDefault(f => (f.Filename ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase));

	private async Task<object> ImportMrpackInternalAsync(InstanceProperties props, string mrpackPath, CancellationToken ct)
	{
		using var zip = ZipFile.OpenRead(mrpackPath);
		var indexEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "modrinth.index.json", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException("modrinth.index.json not found in .mrpack");
		MrpackIndex index;
		using (var s = indexEntry.Open())
			index = (await JsonSerializer.DeserializeAsync<MrpackIndex>(s, JsonOpts, ct)) ?? throw new InvalidOperationException("Invalid modrinth.index.json");

		if (!string.Equals(index.Game?.Trim(), "minecraft", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Only Minecraft modpacks are supported.");
		var depMc = index.Dependencies?.GetValueOrDefault("minecraft")?.Trim() ?? "";
		if (!string.IsNullOrWhiteSpace(props.GameVersion) && depMc.Length > 0 && !string.Equals(depMc, props.GameVersion, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Pack is for Minecraft {depMc}, but instance is {props.GameVersion}.");
		if (!index.Dependencies?.ContainsKey("fabric-loader") ?? true)
			throw new InvalidOperationException("Only Fabric modpacks are supported right now.");

		var downloaded = 0;
		foreach (var f in index.Files ?? new List<MrpackFile>())
		{
			var rel = (f.Path ?? "").Trim().Replace('\\', '/');
			if (rel.Length == 0) continue;
			if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe path in pack: {rel}");
			var serverEnv = f.Env?.Server?.Trim().ToLowerInvariant();
			if (serverEnv == "unsupported") continue;
			var url = (f.Downloads ?? new List<string>()).FirstOrDefault(x => x.StartsWith("http", StringComparison.OrdinalIgnoreCase));
			if (string.IsNullOrWhiteSpace(url)) continue;
			var dst = Path.Combine(props.InstanceDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			var tmp = dst + ".download";
			try
			{
				if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
				await _modrinth.DownloadToFileAsync(url, tmp, ct);
				await VerifyHashesAsync(tmp, f.Hashes ?? new Dictionary<string, string>(), ct);
				System.IO.File.Move(tmp, dst, overwrite: true);
				downloaded++;
			}
			finally { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }
		}
		var overrideFiles = ExtractOverrides(zip, "overrides/", props.InstanceDirectory) + ExtractOverrides(zip, "server-overrides/", props.InstanceDirectory);
		return new { name = index.Name ?? "", versionId = index.VersionId ?? "", downloadedFiles = downloaded, overrideFiles };
	}

	private async Task<byte[]> BuildMrpackExportAsync(InstanceProperties props, CancellationToken ct)
	{
		var modsDir = Path.Combine(props.InstanceDirectory, "mods");
		if (!Directory.Exists(modsDir)) throw new InvalidOperationException("No mods directory found.");
		var modFiles = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).ToList();
		if (modFiles.Count == 0) throw new InvalidOperationException("No enabled mods to export.");
		var fileSha1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in modFiles) fileSha1[p] = await ComputeHashHexAsync(p, SHA1.Create(), ct);
		var byHash = await _modrinth.GetVersionsFromHashesAsync(fileSha1.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "sha1", ct);
		var missing = modFiles.Where(p => !byHash.ContainsKey(fileSha1[p])).Select(Path.GetFileName).ToList();
		if (missing.Count > 0) throw new InvalidOperationException("Unrecognized mods cannot be exported to .mrpack: " + string.Join(", ", missing));

		var files = new List<MrpackFile>();
		foreach (var p in modFiles)
		{
			var sha1 = fileSha1[p];
			var version = byHash[sha1];
			var vf = (version.Files ?? new List<ModrinthVersionFile>()).FirstOrDefault(f => (f.Hashes ?? new Dictionary<string, string>()).TryGetValue("sha1", out var h) && string.Equals(h, sha1, StringComparison.OrdinalIgnoreCase))
				?? SelectInstallableModFile(version.Files ?? new List<ModrinthVersionFile>())
				?? throw new InvalidOperationException($"Failed to map Modrinth file for {Path.GetFileName(p)}");
			files.Add(new MrpackFile { Path = "mods/" + Path.GetFileName(p), Hashes = vf.Hashes ?? new Dictionary<string, string>(), Downloads = new List<string> { vf.Url }, FileSize = vf.Size });
		}

		var index = new MrpackIndex
		{
			FormatVersion = 1,
			Game = "minecraft",
			VersionId = $"{props.InstanceName}-{DateTime.UtcNow:yyyyMMddHHmmss}",
			Name = props.InstanceName,
			Summary = $"Exported from Spawner on {DateTime.UtcNow:O}",
			Files = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
			Dependencies = new Dictionary<string, string> { ["minecraft"] = props.GameVersion ?? "", ["fabric-loader"] = props.FabricLoaderVersion ?? "unknown" }
		};

		using var ms = new MemoryStream();
		using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
		{
			var entry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
			await using var es = entry.Open();
			await JsonSerializer.SerializeAsync(es, index, JsonOpts, ct);

			AddInstanceOverridesToMrpack(zip, props.InstanceDirectory, enabledModFileNames: modFiles.Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x))!);
		}
		return ms.ToArray();
	}

	private static void AddInstanceOverridesToMrpack(ZipArchive zip, string instanceDir, IEnumerable<string> enabledModFileNames)
	{
		var root = Path.GetFullPath(instanceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var enabledMods = new HashSet<string>(enabledModFileNames.Where(x => !string.IsNullOrWhiteSpace(x))!, StringComparer.OrdinalIgnoreCase);

		foreach (var path in Directory.EnumerateFiles(instanceDir, "*", SearchOption.AllDirectories))
		{
			string full;
			try { full = Path.GetFullPath(path); } catch { continue; }
			if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

			var rel = full[root.Length..].Replace('\\', '/');
			if (string.IsNullOrWhiteSpace(rel)) continue;
			if (!ShouldIncludeOverridePath(rel, enabledMods)) continue;

			var entry = zip.CreateEntry("overrides/" + rel, CompressionLevel.Optimal);
			using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var dst = entry.Open();
			src.CopyTo(dst);
		}
	}

	private static bool ShouldIncludeOverridePath(string relativePath, HashSet<string> enabledModFileNames)
	{
		var rel = (relativePath ?? "").Trim().Replace('\\', '/');
		if (rel.Length == 0) return false;

		// Skip runtime / generated noise.
		if (rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)) return false;
		if (rel.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase)) return false;
		if (rel.StartsWith(".spawner/", StringComparison.OrdinalIgnoreCase)) return false;
		if (rel.EndsWith(".download", StringComparison.OrdinalIgnoreCase)) return false;
		if (rel.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return false;

		// Skip the server binary/runtime files.
		if (rel.Equals("server.jar", StringComparison.OrdinalIgnoreCase)) return false;
		if (rel.Equals("server-icon.png.old", StringComparison.OrdinalIgnoreCase)) return false;

		// `mods/*.jar` enabled mods are already represented in modrinth.index.json.
		if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
		{
			var sub = rel["mods/".Length..];
			if (sub.Contains('/'))
			{
				// Allow disabled mods and any nested files to be packed as overrides.
				return true;
			}

			if (sub.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && enabledModFileNames.Contains(sub))
				return false;

			return true;
		}

		// Avoid exporting full world saves by default, but keep datapacks.
		if (rel.StartsWith("world/", StringComparison.OrdinalIgnoreCase))
		{
			if (rel.StartsWith("world/datapacks/", StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		// Include resource packs, config, scripts, properties, etc.
		return true;
	}

	private static int ExtractOverrides(ZipArchive zip, string prefix, string instanceDir)
	{
		var count = 0;
		foreach (var entry in zip.Entries)
		{
			if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
			if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
			var rel = entry.FullName[prefix.Length..].Replace('\\', '/');
			if (string.IsNullOrWhiteSpace(rel)) continue;
			if (!IsSafeRelativePath(rel)) throw new InvalidOperationException($"Unsafe override path: {rel}");
			var dst = Path.Combine(instanceDir, rel.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			using var src = entry.Open();
			using var ds = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
			src.CopyTo(ds);
			count++;
		}
		return count;
	}

	private static bool IsSafeRelativePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return false;
		if (Path.IsPathRooted(path)) return false;
		if (path.Contains("..", StringComparison.Ordinal)) return false;
		return true;
	}

	private static async Task VerifyHashesAsync(string filePath, IReadOnlyDictionary<string, string> hashes, CancellationToken ct)
	{
		if (hashes is null || hashes.Count == 0) return;
		if (hashes.TryGetValue("sha512", out var sha512Expected) && !string.IsNullOrWhiteSpace(sha512Expected))
		{
			var actual = await ComputeHashHexAsync(filePath, SHA512.Create(), ct);
			if (!string.Equals(actual, sha512Expected.Trim(), StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Downloaded file SHA-512 hash mismatch.");
			return;
		}
		if (hashes.TryGetValue("sha1", out var sha1Expected) && !string.IsNullOrWhiteSpace(sha1Expected))
		{
			var actual = await ComputeHashHexAsync(filePath, SHA1.Create(), ct);
			if (!string.Equals(actual, sha1Expected.Trim(), StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Downloaded file SHA-1 hash mismatch.");
		}
	}

	private static async Task<string> ComputeHashHexAsync(string filePath, HashAlgorithm algo, CancellationToken ct)
	{
		await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		var hash = await algo.ComputeHashAsync(fs, ct);
		algo.Dispose();
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private bool TryGetInstanceProps(string serverId, out InstanceProperties props, out IActionResult? error)
	{
		props = new InstanceProperties();
		error = null;
		try
		{
			props = _manager.GetInstanceProperties(serverId);
			if (string.IsNullOrWhiteSpace(props.InstanceDirectory))
			{
				error = NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });
				return false;
			}
			return true;
		}
		catch
		{
			error = NotFound(new { error = new { code = "not_found", message = "Server not found" } });
			return false;
		}
	}

	private bool TryGetFabricInstanceProps(string serverId, out InstanceProperties props, out IActionResult? error)
	{
		if (!TryGetInstanceProps(serverId, out props, out error)) return false;
		if (props.InstanceType != InstanceType.Fabric)
		{
			error = StatusCode(409, new { error = new { code = "unsupported_instance_type", message = "This feature is currently supported only for Fabric instances." } });
			return false;
		}
		return true;
	}

	private static bool TryValidateJarName(string input, out string safeName)
	{
		safeName = "";
		if (string.IsNullOrWhiteSpace(input)) return false;
		var name = input.Trim();
		if (name.Contains('/') || name.Contains('\\')) return false;
		if (name.Contains("..", StringComparison.Ordinal)) return false;
		name = Path.GetFileName(name);
		if (string.IsNullOrWhiteSpace(name)) return false;
		if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
		safeName = name;
		return true;
	}

	private static bool TryValidateSafeContentFileName(string input, out string safeName)
	{
		safeName = "";
		if (string.IsNullOrWhiteSpace(input)) return false;
		var name = input.Trim();
		if (name.Contains('/') || name.Contains('\\')) return false;
		if (name.Contains("..", StringComparison.Ordinal)) return false;
		name = Path.GetFileName(name);
		if (string.IsNullOrWhiteSpace(name)) return false;
		safeName = name;
		return true;
	}

	private sealed class MrpackIndex
	{
		[JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
		[JsonPropertyName("game")] public string? Game { get; set; }
		[JsonPropertyName("versionId")] public string? VersionId { get; set; }
		[JsonPropertyName("name")] public string? Name { get; set; }
		[JsonPropertyName("summary")] public string? Summary { get; set; }
		[JsonPropertyName("files")] public List<MrpackFile>? Files { get; set; }
		[JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
	}

	private sealed class MrpackFile
	{
		[JsonPropertyName("path")] public string? Path { get; set; }
		[JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
		[JsonPropertyName("env")] public MrpackEnv? Env { get; set; }
		[JsonPropertyName("downloads")] public List<string>? Downloads { get; set; }
		[JsonPropertyName("fileSize")] public long? FileSize { get; set; }
	}

	private sealed class MrpackEnv
	{
		[JsonPropertyName("client")] public string? Client { get; set; }
		[JsonPropertyName("server")] public string? Server { get; set; }
	}
}
