using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Spawner.JavaManager
{
	public static class JavaRuntimeManager
	{
		private const string JavaRuntimesDirEnvVar = "SPAWNER_JAVA_RUNTIMES_DIR";

		public static Dictionary<string, string> GetInstalledJavaVersions()
		{
			string javaInstancesLocation = GetJavaRuntimesLocation();
			var versionsPath = Path.Combine(javaInstancesLocation, "java_versions.json");

			if (File.Exists(versionsPath))
			{
				try
				{
					string jsonContent = File.ReadAllText(versionsPath);
					return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent)
						?? new Dictionary<string, string>();
				}
				catch
				{
					TryBackupCorruptJson(versionsPath);
					File.WriteAllText(versionsPath, "{}");
					return new Dictionary<string, string>();
				}
			}

			Dictionary<string, string> emptyDict = new Dictionary<string, string>();
			string emptyJsonContent = System.Text.Json.JsonSerializer.Serialize(emptyDict);
			File.WriteAllText(versionsPath, emptyJsonContent);
			return emptyDict;
		}

		public static void UpdateInstalledJavaVersions(Dictionary<string, string> javaVersions)
		{
			string javaInstancesLocation = GetJavaRuntimesLocation();
			string jsonContent = System.Text.Json.JsonSerializer.Serialize(javaVersions);
			File.WriteAllText(Path.Combine(javaInstancesLocation, "java_versions.json"), jsonContent);
		}

		public static string GetAdoptiumOS()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return "windows";

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return "mac";

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				return File.Exists("/etc/alpine-release")
					? "alpine-linux"
					: "linux";
			}

			throw new PlatformNotSupportedException("Unsupported operating system.");
		}

		public static string GetAdoptiumArchitecture()
		{
			return RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.X64 => "x64",
				Architecture.X86 => "x86",
				Architecture.Arm64 => "aarch64",
				Architecture.Arm => "arm",
				Architecture.S390x => "s390x",
				_ => throw new PlatformNotSupportedException(
					$"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
			};
		}

		public sealed record JavaUpdateCheck(string? CurrentVersion, string? LatestVersion, bool? UpdateAvailable, string Message);

		public static async Task<JavaUpdateCheck> CheckForUpdates(HttpClient client, string javaPath, CancellationToken ct = default)
		{
			var installed = GetInstalledJavaVersions();
			var major = installed.FirstOrDefault(x => string.Equals(x.Value, javaPath,
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Key;
			if (major is null || !File.Exists(javaPath))
				return new(null, null, null, "This Java path is not a Spawner-managed runtime. Check for updates with your Java provider.");

			var releasePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(javaPath))!, "release");
			var current = File.Exists(releasePath)
				? File.ReadLines(releasePath).FirstOrDefault(x => x.StartsWith("JAVA_RUNTIME_VERSION=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim('"')
				: null;
			var package = await GetLatestAdoptiumPackage(client, major, GetAdoptiumOS(), GetAdoptiumArchitecture(), ct)
				?? throw new InvalidOperationException("No Java runtime is available for this platform.");
			var comparison = CompareRuntimeVersions(current, package.Version);
			bool? available = comparison is null ? null : comparison < 0;
			return new(current, package.Version, available, available switch
			{
				true => $"Java update available: {current} → {package.Version}. Update your runtime and set Java Path to the new executable.",
				false => $"Java {current} is up to date.",
				_ => $"Latest Java {major}: {package.Version ?? "unknown"}. The installed build could not be compared."
			});
		}

		internal static int? CompareRuntimeVersions(string? current, string? latest)
		{
			static int[]? Parse(string? value)
			{
				if (string.IsNullOrWhiteSpace(value)) return null;
				// Java 8 uses 1.8.0_XXX-bYY; later releases use MAJOR.MINOR.PATCH+BUILD.
				value = System.Text.RegularExpressions.Regex.Replace(value, @"^1\.8\.0_", "8.0.");
				var match = System.Text.RegularExpressions.Regex.Match(value, @"^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:\+|-b)(\d+)(?:-LTS)?$");
				if (!match.Success) return null;
				return Enumerable.Range(1, 5).Select(i => int.TryParse(match.Groups[i].Value, out var n) ? n : 0).ToArray();
			}
			var a = Parse(current);
			var b = Parse(latest);
			if (a is null || b is null) return null;
			for (var i = 0; i < a.Length; i++)
				if (a[i] != b[i]) return a[i].CompareTo(b[i]);
			return 0;
		}

		public static async Task InstallNewJavaVersion(
			HttpClient client,
			string javaVersion,
			Func<JavaDownloadProgress, Task>? onProgress = null,
			CancellationToken ct = default)
		{
			var installedJavaVersions = GetInstalledJavaVersions();
			if (installedJavaVersions.TryGetValue(javaVersion, out var existingJavaPath))
			{
				if (!string.IsNullOrWhiteSpace(existingJavaPath) && File.Exists(existingJavaPath))
					return;

				installedJavaVersions.Remove(javaVersion);
				UpdateInstalledJavaVersions(installedJavaVersions);
			}

			string os = GetAdoptiumOS();
			string architecture = GetAdoptiumArchitecture();

			string jreFinalDir = Path.Combine(GetJavaRuntimesLocation(), javaVersion);
			var javaBinary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java";
			var javaBinaryPath = Path.Combine(jreFinalDir, "bin", javaBinary);

			var package = await GetLatestAdoptiumPackage(client, javaVersion, os, architecture, ct)
				?? throw new Exception($"No usable Adoptium runtime found for Java version {javaVersion} on {os} {architecture}");

			string downloadUrl = package.Link;
			long jreSize = package.Size;
			string jreSha256 = package.Checksum;
			string jreFileName = package.Name;

			string jreDownloadPath = Path.Combine(GetJavaRuntimesLocation(), "jreZipDownloads", javaVersion, jreFileName);

			var progress = new Progress<(long received, long? total)>(p =>
			{
				var (received, total) = p;
				if (onProgress != null)
				{
					_ = onProgress(new JavaDownloadProgress(
						javaVersion,
						jreFileName,
						received,
						total
					));
				}
			});

			await Download.DownloadFile(client, downloadUrl, jreDownloadPath, progress, jreSize, (HashAlgorithmName.SHA256, jreSha256), ct);
			ExtractJre(jreDownloadPath, jreFinalDir);
			if (!File.Exists(javaBinaryPath))
				throw new FileNotFoundException($"Java runtime extracted, but the launcher was not found at '{javaBinaryPath}'.");

			var downloadsDir = Path.Combine(GetJavaRuntimesLocation(), "jreZipDownloads", javaVersion);
			if (Directory.Exists(downloadsDir))
				Directory.Delete(downloadsDir, true);

			installedJavaVersions[javaVersion] = javaBinaryPath;
			UpdateInstalledJavaVersions(installedJavaVersions);
		}

		private static void ExtractJre(string jreDownloadPath, string jreFinalDir)
		{
			if (Directory.Exists(jreFinalDir))
				Directory.Delete(jreFinalDir, recursive: true);

			var tempExtractDir = Path.Combine(Path.GetTempPath(), "spawner-jre-extract-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempExtractDir);

			try
			{
				var lower = Path.GetFileName(jreDownloadPath).ToLowerInvariant();

				if (lower.EndsWith(".zip", StringComparison.Ordinal))
				{
					ZipFile.ExtractToDirectory(jreDownloadPath, tempExtractDir, overwriteFiles: true);
				}
				else if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
				{
					using var fs = File.OpenRead(jreDownloadPath);
					using var gz = new GZipStream(fs, CompressionMode.Decompress);
					TarFile.ExtractToDirectory(gz, tempExtractDir, overwriteFiles: true);
				}
				else
				{
					throw new NotSupportedException($"Unsupported Java archive format: {jreDownloadPath}");
				}

				// Adoptium archives usually contain a single top-level folder (e.g. jdk-xx/).
				// Normalize by flattening that folder into the target runtime directory.
				var topDirs = Directory.GetDirectories(tempExtractDir, "*", SearchOption.TopDirectoryOnly);
				var topFiles = Directory.GetFiles(tempExtractDir, "*", SearchOption.TopDirectoryOnly);
				var extractRoot = (topFiles.Length == 0 && topDirs.Length == 1) ? topDirs[0] : tempExtractDir;

				Directory.CreateDirectory(jreFinalDir);
				CopyDirectoryRecursive(extractRoot, jreFinalDir);

				// Ensure java binary is executable on Unix if permission bits were not preserved.
				if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					var javaPath = Path.Combine(jreFinalDir, "bin", "java");
					if (File.Exists(javaPath))
					{
						try
						{
							var mode = File.GetUnixFileMode(javaPath);
							mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
							File.SetUnixFileMode(javaPath, mode);
						}
						catch
						{
							// best-effort
						}
					}
				}
			}
			finally
			{
				try
				{
					if (Directory.Exists(tempExtractDir))
						Directory.Delete(tempExtractDir, recursive: true);
				}
				catch
				{
					// best-effort cleanup
				}
			}
		}

		public static string GetJavaRuntimesLocation()
		{
			var configured = (Environment.GetEnvironmentVariable(JavaRuntimesDirEnvVar) ?? "").Trim();
			if (configured.Length > 0)
			{
				Directory.CreateDirectory(configured);
				return configured;
			}

			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrWhiteSpace(appData))
				appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrWhiteSpace(appData))
				appData = AppContext.BaseDirectory;

			string javaInstancesLocation = Path.Combine(appData, "Spawner", "JavaRuntimes");
			if (!Directory.Exists(javaInstancesLocation))
				Directory.CreateDirectory(javaInstancesLocation);
			return javaInstancesLocation;
		}

		private static async Task<AdoptiumPackage?> GetLatestAdoptiumPackage(
			HttpClient client,
			string javaVersion,
			string os,
			string architecture,
			CancellationToken ct)
		{
			foreach (var imageType in new[] { "jre", "jdk" })
			{
				string assetsUrl = $"https://api.adoptium.net/v3/assets/latest/{javaVersion}/hotspot?os={os}&architecture={architecture}&image_type={imageType}&vendor=eclipse";

				var assetsResult = await client.GetStringAsync(assetsUrl, ct);
				JsonNode? assets = JsonNode.Parse(assetsResult);
				var first = assets?.AsArray().FirstOrDefault();
				if (first is null) continue;

				var pkg = first["binary"]?["package"];
				var link = pkg?["link"]?.ToString();
				var checksum = pkg?["checksum"]?.ToString();
				var size = pkg?["size"]?.GetValue<long>();
				var name = pkg?["name"]?.ToString();
				if (string.IsNullOrWhiteSpace(link) ||
					string.IsNullOrWhiteSpace(checksum) ||
					size is null ||
					size.Value <= 0)
				{
					continue;
				}

				return new AdoptiumPackage(
					link,
					checksum,
					size.Value,
					string.IsNullOrWhiteSpace(name) ? $"java-{javaVersion}-{imageType}" : name,
					first["version"]?["openjdk_version"]?.ToString());
			}

			return null;
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

		private sealed record AdoptiumPackage(string Link, string Checksum, long Size, string Name, string? Version);
	}

	public sealed record JavaDownloadProgress(
		string JavaVersion,
		string FileName,
		long BytesReceived,
		long? TotalBytes
	);
}
