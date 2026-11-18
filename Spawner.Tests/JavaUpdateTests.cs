using Spawner.JavaManager;
using Spawner.Controllers;

namespace Spawner.Tests;

public sealed class JavaUpdateTests
{
    [Fact]
    public void FabricLoaderSelectionHonorsExplicitVersionAndRejectsUnknownVersions()
    {
        var loaders = new HashSet<string> { "0.16.9", "0.16.10" };
        Assert.Equal("0.16.10", ApiV1Controller.SelectPreferredFabricLoaderVersion(loaders, "0.16.9", "0.16.10"));
        Assert.Equal("0.16.9", ApiV1Controller.SelectPreferredFabricLoaderVersion(loaders, "0.16.9"));
        Assert.Throws<InvalidOperationException>(() => ApiV1Controller.SelectPreferredFabricLoaderVersion(loaders, "0.16.9", "0.99.0"));
        Assert.Throws<InvalidOperationException>(() => ApiV1Controller.SelectPreferredFabricLoaderVersion(loaders, "0.16.9", ""));
    }

    [Theory]
    [InlineData("21.0.4+7-LTS", "21.0.5+11-LTS", -1)]
    [InlineData("21.0.5+11-LTS", "21.0.5+11-LTS", 0)]
    [InlineData("21.0.5+12", "21.0.5+11", 1)]
    [InlineData("1.8.0_432-b06", "8.0.442+6", -1)]
    [InlineData("17.0.10+7", "17.0.9+9", 1)]
    [InlineData("21.0.5.1+1", "21.0.5+11", 1)]
    public void ComparesRuntimeBuilds(string current, string latest, int expected)
    {
        Assert.Equal(expected, JavaRuntimeManager.CompareRuntimeVersions(current, latest));
    }

    [Theory]
    [InlineData(null, "21.0.5+11")]
    [InlineData("21.0.5+11", null)]
    [InlineData("unknown", "21.0.5+11")]
    [InlineData("22-ea+1", "22.0.0+36")]
    public void DoesNotClaimUnknownBuildIsCurrent(string? current, string? latest)
    {
        Assert.Null(JavaRuntimeManager.CompareRuntimeVersions(current, latest));
    }
}
