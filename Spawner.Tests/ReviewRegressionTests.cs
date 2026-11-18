using System.IO.Compression;
using System.Text.Json;
using Spawner.Controllers;

namespace Spawner.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spawner_review_" + Guid.NewGuid().ToString("N"));

    public ReviewRegressionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WebSocketArguments_ReadDeserializedJsonStrings()
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"serverId":"abc","command":"say hello"}""");
        Assert.Equal("abc", WsV1Controller.GetRequiredArgument(args, "serverId"));
        Assert.Equal("say hello", WsV1Controller.GetRequiredArgument(args, "command"));
        Assert.Throws<ArgumentException>(() => WsV1Controller.GetRequiredArgument(args, "missing"));
        var invalid = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"serverId":42}""");
        Assert.Throws<ArgumentException>(() => WsV1Controller.GetRequiredArgument(invalid, "serverId"));
    }

    [Fact]
    public void ModReplacement_RestoresOldFilesWhenCommitFails()
    {
        var old = Path.Combine(_dir, "old.jar");
        File.WriteAllText(old, "working mod");
        Assert.Throws<FileNotFoundException>(() => ModrinthV1Controller.CommitModReplacement(
            Path.Combine(_dir, "missing.download"), Path.Combine(_dir, "new.jar"), [old], false));
        Assert.Equal("working mod", File.ReadAllText(old));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ModReplacement_ReplacesSameNameAndRemovesDisabledCopy()
    {
        var destination = Path.Combine(_dir, "mod.jar");
        var disabledDir = Path.Combine(_dir, ".disabled");
        Directory.CreateDirectory(disabledDir);
        var disabled = Path.Combine(disabledDir, "old.jar");
        var temporary = Path.Combine(_dir, "new.download");
        File.WriteAllText(destination, "old");
        File.WriteAllText(disabled, "disabled old");
        File.WriteAllText(temporary, "verified new");
        ModrinthV1Controller.CommitModReplacement(temporary, destination, [destination, disabled], false);
        Assert.Equal("verified new", File.ReadAllText(destination));
        Assert.False(File.Exists(disabled));
        Assert.Single(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModpackExport_WritesIndexAndOverrides()
    {
        File.WriteAllText(Path.Combine(_dir, "server.properties"), "motd=Test");
        var bytes = await ModrinthV1Controller.BuildMrpackArchiveAsync(new { name = "Test pack" }, _dir, [], CancellationToken.None);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var index = JsonDocument.Parse(zip.GetEntry("modrinth.index.json")!.Open());
        Assert.Equal("Test pack", index.RootElement.GetProperty("name").GetString());
        using var reader = new StreamReader(zip.GetEntry("overrides/server.properties")!.Open());
        Assert.Equal("motd=Test", await reader.ReadToEndAsync());
    }
}
