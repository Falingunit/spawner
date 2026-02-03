using System;
using System.Collections.Generic;
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

		public static async Task InstallNewJavaVersion(
			HttpClient client,
			string javaVersion,
			Func<JavaDownloadProgress, Task>? onProgress = null,
			CancellationToken ct = default)
		{
			var installedJavaVersions = GetInstalledJavaVersions();
			if (installedJavaVersions.ContainsKey(javaVersion)) return;

			string os = GetAdoptiumOS();
			string architecture = GetAdoptiumArchitecture();

			string assetsUrl = $"https://api.adoptium.net/v3/assets/latest/{javaVersion}/hotspot?os={os}&architecture={architecture}&image_type=jre&vendor=eclipse";

			var assetsResult = await client.GetStringAsync(assetsUrl, ct);
			JsonNode? assets = JsonNode.Parse(assetsResult);

			if (assets == null || assets.AsArray().Count == 0)
				throw new Exception($"No assets found for Java version {javaVersion} on {os} {architecture}");

			string downloadUrl = assets[0]?["binary"]?["package"]?["link"]?.ToString()
				?? throw new Exception("Download link not found in assets.");
			long jreSize = assets[0]?["binary"]?["package"]?["size"]?.GetValue<long>()
				?? throw new Exception("JRE size not found in assets.");
			string jreSha256 = assets[0]?["binary"]?["package"]?["checksum"]?.ToString()
				?? throw new Exception("JRE SHA256 not found in assets.");
			string jreFileName = assets[0]?["binary"]?["package"]?["name"]?.ToString() ?? $"jre-{javaVersion}.zip";

			string jreDownloadPath = Path.Combine(GetJavaRuntimesLocation(), "jreZipDownloads", javaVersion, jreFileName);
			string jreFinalDir = Path.Combine(GetJavaRuntimesLocation(), javaVersion);

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

			var downloadsDir = Path.Combine(GetJavaRuntimesLocation(), "jreZipDownloads", javaVersion);
			if (Directory.Exists(downloadsDir))
				Directory.Delete(downloadsDir, true);

			var javaBinary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java";
			installedJavaVersions[javaVersion] = Path.Combine(jreFinalDir, "bin", javaBinary);
			UpdateInstalledJavaVersions(installedJavaVersions);
		}

		private static void ExtractJre(string jreDownloadPath, string jreFinalDir)
		{
			using var archive = ZipFile.OpenRead(jreDownloadPath);

			foreach (var entry in archive.Entries)
			{
				if (string.IsNullOrEmpty(entry.Name))
					continue;

				var parts = entry.FullName.Split('/', '\\');
				if (parts.Length <= 1)
					continue;

				string relativePath = Path.Combine(parts[1..]);
				string destinationPath = Path.Combine(jreFinalDir, relativePath);

				var dir = Path.GetDirectoryName(destinationPath);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);

				entry.ExtractToFile(destinationPath, overwrite: true);
			}
		}

		public static string GetJavaRuntimesLocation()
		{
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			string javaInstancesLocation = Path.Combine(appData, "Spawner", "JavaRuntimes");
			if (!Directory.Exists(javaInstancesLocation))
				Directory.CreateDirectory(javaInstancesLocation);
			return javaInstancesLocation;
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

	public sealed record JavaDownloadProgress(
		string JavaVersion,
		string FileName,
		long BytesReceived,
		long? TotalBytes
	);
}
