using System.Security.Cryptography;

namespace Spawner
{
	public static class Download
	{
		public static async Task DownloadFile(
			HttpClient client,
			string url,
			string destinationPath,
			IProgress<(long received, long? total)>? progress = null,
			long? totalBytes = null,
			(HashAlgorithmName algo, string expectedHex)? expectedHash = null,
			CancellationToken ct = default)
		{
			var dir = Path.GetDirectoryName(destinationPath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
			response.EnsureSuccessStatusCode();

			totalBytes ??= response.Content.Headers.ContentLength;

			await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
			await using var fileStream = new FileStream(
				destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
				bufferSize: 81920, useAsync: true);

			IncrementalHash? hasher = expectedHash is not null
				? IncrementalHash.CreateHash(expectedHash.Value.algo)
				: null;

			var buffer = new byte[81920];
			long receivedBytes = 0;

			int read;
			while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
			{
				await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);

				hasher?.AppendData(buffer, 0, read);

				receivedBytes += read;
				progress?.Report((receivedBytes, totalBytes));
			}

			if (expectedHash is not null)
			{
				try
				{
					var (algo, expectedHex) = expectedHash.Value;

					string actualHex = Convert.ToHexString(hasher!.GetHashAndReset())
						.ToLowerInvariant();
					string normalizedExpected = NormalizeHex(expectedHex);

					if (!actualHex.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
						throw new InvalidOperationException(
							$"{algo.Name} mismatch. Expected {normalizedExpected}, got {actualHex}");
				}
				catch
				{
					try
					{
						if (File.Exists(destinationPath))
							File.Delete(destinationPath);
					}
					catch
					{
						// intentionally swallow cleanup errors
					}

					throw;
				}
			}
		}

		private static string NormalizeHex(string hex)
		{
			var sb = new System.Text.StringBuilder(hex.Length);
			foreach (char c in hex)
				if (Uri.IsHexDigit(c))
					sb.Append(char.ToLowerInvariant(c));
			return sb.ToString();
		}
	}
}
