using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using System.IO.Compression;
using System.Text;

namespace Spawner.Controllers;

[ApiController]
[Route("api/v1/servers/{serverId}/files")]
public sealed class FilesV1Controller : ControllerBase
{
	private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

	private readonly InstanceManager _manager;

	public FilesV1Controller(InstanceManager manager)
	{
		_manager = manager;
	}

	[HttpGet("list")]
	public IActionResult List([FromRoute] string serverId, [FromQuery] string? path = null)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path ?? "", out var full, out var err)) return err!;

		if (!Directory.Exists(full))
			return NotFound(new { error = new { code = "not_found", message = "Directory not found" } });

		var entries = Directory.EnumerateFileSystemEntries(full)
			.Select(p => new FileSystemInfoWrapper(p))
			.OrderByDescending(e => e.IsDir)
			.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
			.Select(e => new
			{
				name = e.Name,
				path = ToRel(root, e.FullPath),
				isDir = e.IsDir,
				size = e.IsDir ? 0 : e.Length,
				lastWriteTimeUtc = e.LastWriteTimeUtc.ToString("O")
			})
			.ToList();

		return Ok(new
		{
			path = ToRel(root, full),
			entries,
			serverTime = DateTime.UtcNow.ToString("O")
		});
	}

	[HttpGet("text")]
	public IActionResult ReadText([FromRoute] string serverId, [FromQuery] string path)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		if (!System.IO.File.Exists(full))
			return NotFound(new { error = new { code = "not_found", message = "File not found" } });

		var fi = new FileInfo(full);
		if (fi.Length > 2 * 1024 * 1024)
			return StatusCode(413, new { error = new { code = "too_large", message = "File too large to edit" } });

		// Heuristic: treat files containing null bytes as binary.
		if (LooksBinary(full, maxScanBytes: 32 * 1024))
			return StatusCode(415, new { error = new { code = "unsupported", message = "File appears to be binary" } });

		string content;
		try
		{
			content = System.IO.File.ReadAllText(full, Encoding.UTF8);
		}
		catch (DecoderFallbackException)
		{
			return StatusCode(415, new { error = new { code = "unsupported", message = "File is not valid UTF-8 text" } });
		}

		return Ok(new
		{
			path = ToRel(root, full),
			content,
			size = fi.Length,
			lastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O")
		});
	}

	public sealed record WriteTextReq(string content);

	[HttpPut("text")]
	public IActionResult WriteText([FromRoute] string serverId, [FromQuery] string path, [FromBody] WriteTextReq body)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		var dir = Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

		var content = body?.content ?? "";
		if (content.Length > 2 * 1024 * 1024)
			return StatusCode(413, new { error = new { code = "too_large", message = "Content too large" } });

		System.IO.File.WriteAllText(full, content, Encoding.UTF8);
		var fi = new FileInfo(full);
		return Ok(new { path = ToRel(root, full), size = fi.Length, lastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O") });
	}

	[HttpPost("create-text")]
	public IActionResult CreateTextFile([FromRoute] string serverId, [FromQuery] string path, [FromBody] WriteTextReq? body = null)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		if (Directory.Exists(full))
			return StatusCode(409, new { error = new { code = "conflict", message = "A directory exists at that path" } });

		var dir = Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

		if (System.IO.File.Exists(full))
			return StatusCode(409, new { error = new { code = "conflict", message = "File already exists" } });

		System.IO.File.WriteAllText(full, body?.content ?? "", Encoding.UTF8);
		var fi = new FileInfo(full);
		return Ok(new { path = ToRel(root, full), size = fi.Length, lastWriteTimeUtc = fi.LastWriteTimeUtc.ToString("O") });
	}

	[HttpPost("mkdir")]
	public IActionResult Mkdir([FromRoute] string serverId, [FromQuery] string path)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path ?? "", out var full, out var err)) return err!;

		if (System.IO.File.Exists(full))
			return StatusCode(409, new { error = new { code = "conflict", message = "A file exists at that path" } });

		Directory.CreateDirectory(full);
		var di = new DirectoryInfo(full);
		return Ok(new { path = ToRel(root, full), lastWriteTimeUtc = di.LastWriteTimeUtc.ToString("O") });
	}

	public sealed record MoveCopyReq(string src, string dst);

	[HttpPost("move")]
	public IActionResult Move([FromRoute] string serverId, [FromBody] MoveCopyReq body)
	{
		if (body is null || string.IsNullOrWhiteSpace(body.src) || string.IsNullOrWhiteSpace(body.dst))
			return BadRequest(new { error = new { code = "bad_request", message = "src and dst are required" } });

		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, body.src, out var srcFull, out var err1)) return err1!;
		if (!TryResolveUnderRoot(root, body.dst, out var dstFull, out var err2)) return err2!;

		if (!System.IO.File.Exists(srcFull) && !Directory.Exists(srcFull))
			return NotFound(new { error = new { code = "not_found", message = "Source path not found" } });

		if (System.IO.File.Exists(dstFull) || Directory.Exists(dstFull))
			return StatusCode(409, new { error = new { code = "conflict", message = "Destination already exists" } });

		var dstDir = Path.GetDirectoryName(dstFull);
		if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

		try
		{
			if (System.IO.File.Exists(srcFull))
				System.IO.File.Move(srcFull, dstFull);
			else
				Directory.Move(srcFull, dstFull);
		}
		catch (IOException ex)
		{
			return StatusCode(409, new { error = new { code = "conflict", message = ex.Message } });
		}

		return Ok(new { src = body.src, dst = ToRel(root, dstFull) });
	}

	[HttpPost("copy")]
	public IActionResult Copy([FromRoute] string serverId, [FromBody] MoveCopyReq body)
	{
		if (body is null || string.IsNullOrWhiteSpace(body.src) || string.IsNullOrWhiteSpace(body.dst))
			return BadRequest(new { error = new { code = "bad_request", message = "src and dst are required" } });

		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, body.src, out var srcFull, out var err1)) return err1!;
		if (!TryResolveUnderRoot(root, body.dst, out var dstFull, out var err2)) return err2!;

		if (!System.IO.File.Exists(srcFull) && !Directory.Exists(srcFull))
			return NotFound(new { error = new { code = "not_found", message = "Source path not found" } });

		if (System.IO.File.Exists(dstFull) || Directory.Exists(dstFull))
			return StatusCode(409, new { error = new { code = "conflict", message = "Destination already exists" } });

		var dstDir = Path.GetDirectoryName(dstFull);
		if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

		try
		{
			if (System.IO.File.Exists(srcFull))
			{
				System.IO.File.Copy(srcFull, dstFull);
			}
			else
			{
				// Prevent copying a directory into itself.
				if (IsUnderRoot(srcFull, dstFull))
					return BadRequest(new { error = new { code = "bad_request", message = "Destination cannot be inside source directory" } });

				CopyDirectoryRecursive(srcFull, dstFull);
			}
		}
		catch (IOException ex)
		{
			return StatusCode(409, new { error = new { code = "conflict", message = ex.Message } });
		}

		return Ok(new { src = body.src, dst = ToRel(root, dstFull) });
	}

	[HttpDelete]
	public IActionResult Delete([FromRoute] string serverId, [FromQuery] string path, [FromQuery] bool recursive = false)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		if (System.IO.File.Exists(full))
		{
			System.IO.File.Delete(full);
			return Ok(new { deleted = true });
		}

		if (Directory.Exists(full))
		{
			Directory.Delete(full, recursive);
			return Ok(new { deleted = true });
		}

		return NotFound(new { error = new { code = "not_found", message = "Path not found" } });
	}

	[HttpPost("upload")]
	[RequestSizeLimit(200 * 1024 * 1024)]
	public async Task<IActionResult> Upload([FromRoute] string serverId, [FromQuery] string? path = null, CancellationToken ct = default)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path ?? "", out var targetDir, out var err)) return err!;

		Directory.CreateDirectory(targetDir);

		if (!Request.HasFormContentType)
			return BadRequest(new { error = new { code = "bad_request", message = "multipart/form-data required" } });

		var form = await Request.ReadFormAsync(ct);
		if (form.Files.Count == 0)
			return BadRequest(new { error = new { code = "bad_request", message = "No files uploaded" } });

		var saved = new List<object>();

		foreach (var file in form.Files)
		{
			var name = Path.GetFileName(file.FileName);
			if (string.IsNullOrWhiteSpace(name)) continue;

			var dest = Path.Combine(targetDir, name);
			await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
			await file.CopyToAsync(fs, ct);
			saved.Add(new { name, path = ToRel(root, dest) });
		}

		return Ok(new { saved });
	}

	[HttpGet("raw")]
	public IActionResult Raw([FromRoute] string serverId, [FromQuery] string path, [FromQuery] bool download = false)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		if (!System.IO.File.Exists(full))
			return NotFound(new { error = new { code = "not_found", message = "File not found" } });

		if (!ContentTypeProvider.TryGetContentType(full, out var ct))
			ct = "application/octet-stream";

		var fileName = Path.GetFileName(full);
		if (download)
			return PhysicalFile(full, ct, fileName);

		return PhysicalFile(full, ct);
	}

	[HttpGet("zip")]
	public IActionResult Zip([FromRoute] string serverId, [FromQuery] string path)
	{
		if (!TryGetRoot(serverId, out var root, out var notFound)) return notFound!;
		if (!TryResolveUnderRoot(root, path, out var full, out var err)) return err!;

		if (!Directory.Exists(full))
			return NotFound(new { error = new { code = "not_found", message = "Directory not found" } });

		var zipName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (string.IsNullOrWhiteSpace(zipName)) zipName = "folder";
		zipName += ".zip";

		Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";
		Response.ContentType = "application/zip";

		using var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true);
		AddDirectoryToZip(archive, root, full);
		return new EmptyResult();
	}

	private static void AddDirectoryToZip(ZipArchive archive, string root, string dir)
	{
		foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
		{
			var rel = ToRel(root, file);
			var entry = archive.CreateEntry(rel, CompressionLevel.Fastest);
			using var entryStream = entry.Open();
			using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			fs.CopyTo(entryStream);
		}
	}

	private static void CopyDirectoryRecursive(string srcDir, string dstDir)
	{
		Directory.CreateDirectory(dstDir);

		foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(file);
			System.IO.File.Copy(file, Path.Combine(dstDir, name), overwrite: false);
		}

		foreach (var sub in Directory.EnumerateDirectories(srcDir, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(sub);
			CopyDirectoryRecursive(sub, Path.Combine(dstDir, name));
		}
	}

	private bool TryGetRoot(string serverId, out string root, out IActionResult? error)
	{
		error = null;
		root = "";

		Spawner.InstanceProperties instProps;
		try { instProps = _manager.GetInstanceProperties(serverId); }
		catch
		{
			error = NotFound(new { error = new { code = "not_found", message = "Server not found" } });
			return false;
		}

		root = instProps.InstanceDirectory;
		if (string.IsNullOrWhiteSpace(root))
		{
			error = NotFound(new { error = new { code = "not_found", message = "Server directory not found" } });
			return false;
		}

		root = Path.GetFullPath(root);
		return true;
	}

	private static bool TryResolveUnderRoot(string rootFull, string relative, out string fullPath, out IActionResult? error)
	{
		error = null;
		fullPath = "";

		relative = (relative ?? "").Replace('/', Path.DirectorySeparatorChar).Trim();
		if (relative.Length == 0)
		{
			fullPath = rootFull;
			return true;
		}

		if (Path.IsPathRooted(relative) || relative.Contains(':'))
		{
			error = new BadRequestObjectResult(new { error = new { code = "bad_request", message = "Invalid path" } });
			return false;
		}

		var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
		if (!IsUnderRoot(rootFull, combined))
		{
			error = new BadRequestObjectResult(new { error = new { code = "bad_request", message = "Path escapes instance root" } });
			return false;
		}

		fullPath = combined;
		return true;
	}

	private static bool IsUnderRoot(string rootFull, string candidateFull)
	{
		var root = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return candidateFull.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
			   string.Equals(candidateFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
	}

	private static string ToRel(string rootFull, string fullPath)
	{
		var rel = Path.GetRelativePath(rootFull, fullPath);
		return rel.Replace('\\', '/');
	}

	private static bool LooksBinary(string path, int maxScanBytes)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var buffer = new byte[Math.Min(maxScanBytes, (int)Math.Min(fs.Length, maxScanBytes))];
			var read = fs.Read(buffer, 0, buffer.Length);
			for (var i = 0; i < read; i++)
				if (buffer[i] == 0) return true;
			return false;
		}
		catch
		{
			return true;
		}
	}

	private sealed class FileSystemInfoWrapper
	{
		public string FullPath { get; }
		public string Name { get; }
		public bool IsDir { get; }
		public long Length { get; }
		public DateTime LastWriteTimeUtc { get; }

		public FileSystemInfoWrapper(string path)
		{
			FullPath = path;
			Name = Path.GetFileName(path);
			IsDir = Directory.Exists(path);
			if (!IsDir)
			{
				var fi = new FileInfo(path);
				Length = fi.Length;
				LastWriteTimeUtc = fi.LastWriteTimeUtc;
			}
			else
			{
				var di = new DirectoryInfo(path);
				Length = 0;
				LastWriteTimeUtc = di.LastWriteTimeUtc;
			}
		}
	}
}
