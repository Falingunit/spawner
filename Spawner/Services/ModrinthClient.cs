using System.Net.Http.Headers;
using System.Text.Json;

namespace Spawner.Services;

public sealed class ModrinthClient : IDisposable
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		PropertyNameCaseInsensitive = true
	};

	private readonly HttpClient _http;

	public ModrinthClient()
	{
		_http = new HttpClient
		{
			BaseAddress = new Uri("https://api.modrinth.com/v2/"),
			Timeout = TimeSpan.FromSeconds(30)
		};
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("Spawner/0.1 (+https://local.spawner)");
	}

	public async Task<ModrinthSearchResponse> SearchProjectsAsync(
		string? query,
		string? projectType,
		string? category,
		string? loader,
		string? gameVersion,
		int offset,
		int limit,
		CancellationToken ct)
	{
		limit = Math.Clamp(limit, 1, 100);
		offset = Math.Max(0, offset);

		var facets = new List<List<string>>();
		if (!string.IsNullOrWhiteSpace(projectType)) facets.Add(new() { $"project_type:{projectType.Trim().ToLowerInvariant()}" });
		if (!string.IsNullOrWhiteSpace(category)) facets.Add(new() { $"categories:{category.Trim().ToLowerInvariant()}" });
		if (!string.IsNullOrWhiteSpace(loader)) facets.Add(new() { $"categories:{loader.Trim().ToLowerInvariant()}" });
		if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add(new() { $"versions:{gameVersion.Trim()}" });

		var qs = new List<string>
		{
			"limit=" + Uri.EscapeDataString(limit.ToString()),
			"offset=" + Uri.EscapeDataString(offset.ToString())
		};
		if (!string.IsNullOrWhiteSpace(query)) qs.Add("query=" + Uri.EscapeDataString(query.Trim()));
		if (facets.Count > 0)
		{
			var facetsJson = JsonSerializer.Serialize(facets);
			qs.Add("facets=" + Uri.EscapeDataString(facetsJson));
		}

		return await GetJsonAsync<ModrinthSearchResponse>("search?" + string.Join("&", qs), ct);
	}

	public Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct) =>
		GetJsonAsync<ModrinthProject>($"project/{Uri.EscapeDataString(idOrSlug)}", ct);

	public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(
		string idOrSlug,
		IReadOnlyList<string>? loaders,
		IReadOnlyList<string>? gameVersions,
		bool includeChangelog,
		CancellationToken ct)
	{
		var qs = new List<string>();
		if (loaders is { Count: > 0 })
			qs.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(loaders)));
		if (gameVersions is { Count: > 0 })
			qs.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(gameVersions)));
		if (!includeChangelog)
			qs.Add("include_changelog=false");

		var path = $"project/{Uri.EscapeDataString(idOrSlug)}/version";
		if (qs.Count > 0) path += "?" + string.Join("&", qs);
		return await GetJsonAsync<List<ModrinthVersion>>(path, ct);
	}

	public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct) =>
		GetJsonAsync<ModrinthVersion>($"version/{Uri.EscapeDataString(versionId)}", ct);

	public async Task<Dictionary<string, ModrinthVersion>> GetLatestVersionsFromHashesAsync(
		IReadOnlyList<string> hashes,
		string algorithm,
		IReadOnlyList<string> loaders,
		IReadOnlyList<string> gameVersions,
		CancellationToken ct)
	{
		var body = new
		{
			hashes,
			algorithm,
			loaders,
			game_versions = gameVersions
		};

		return await PostJsonAsync<Dictionary<string, ModrinthVersion>>("version_files/update", body, ct);
	}

	public async Task<Dictionary<string, ModrinthVersion>> GetVersionsFromHashesAsync(
		IReadOnlyList<string> hashes,
		string algorithm,
		CancellationToken ct)
	{
		var body = new
		{
			hashes,
			algorithm
		};

		return await PostJsonAsync<Dictionary<string, ModrinthVersion>>("version_files", body, ct);
	}

	private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
	{
		using var res = await _http.GetAsync(path, ct);
		return await ReadJsonAsync<T>(res, ct);
	}

	private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
	{
		using var res = await _http.PostAsJsonAsync(path, body, JsonOpts, ct);
		return await ReadJsonAsync<T>(res, ct);
	}

	private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct)
	{
		var text = await res.Content.ReadAsStringAsync(ct);
		if (!res.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
				? $"{(int)res.StatusCode} {res.ReasonPhrase}"
				: text);
		}

		var parsed = JsonSerializer.Deserialize<T>(text, JsonOpts);
		if (parsed is null) throw new InvalidOperationException("Invalid Modrinth response.");
		return parsed;
	}

	public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
	{
		using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!res.IsSuccessStatusCode)
			throw new InvalidOperationException($"Failed to download file ({(int)res.StatusCode} {res.ReasonPhrase}).");

		Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
		await using var src = await res.Content.ReadAsStreamAsync(ct);
		await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
		await src.CopyToAsync(dst, ct);
	}

	public void Dispose() => _http.Dispose();
}

public sealed record ModrinthSearchResponse(
	int? TotalHits,
	int? Offset,
	int? Limit,
	List<ModrinthSearchHit>? Hits
);

public sealed record ModrinthSearchHit(
	string ProjectId,
	string? Slug,
	string? Title,
	string? Author,
	string? Description,
	string? ProjectType,
	string? IconUrl,
	List<string>? Categories,
	List<string>? Versions,
	string? ClientSide,
	string? ServerSide,
	long? Downloads,
	DateTimeOffset? DateModified,
	DateTimeOffset? DateCreated
);

public sealed record ModrinthProject(
	string Id,
	string? Slug,
	string? Title,
	string? Description,
	string? ProjectType,
	string? IconUrl,
	List<string>? Categories,
	string? ClientSide,
	string? ServerSide,
	DateTimeOffset? Published,
	DateTimeOffset? Updated
);

public sealed record ModrinthVersion(
	string Id,
	string ProjectId,
	string? Name,
	string? VersionNumber,
	string? VersionType,
	string? Status,
	bool? Featured,
	List<string>? Loaders,
	List<string>? GameVersions,
	List<ModrinthVersionFile>? Files,
	List<ModrinthDependency>? Dependencies
);

public sealed record ModrinthVersionFile(
	Dictionary<string, string>? Hashes,
	string Url,
	string Filename,
	bool? Primary,
	long? Size,
	string? FileType
);

public sealed record ModrinthDependency(
	string? VersionId,
	string? ProjectId,
	string? FileName,
	string? DependencyType
);
