namespace Spawner.Tests;

public sealed class PlayerCountTests
{
    private static Instance CreateInstance()
    {
        var instance = new Instance(new InstanceProperties(), new Settings());
        instance.SetStatus(InstanceStatus.Online);
        return instance;
    }

    [Fact]
    public void DuplicateLoginAndJoinCountOnePlayer()
    {
        var instance = CreateInstance();
        var counts = new List<int>();
        instance.OnPlayersChanged += counts.Add;
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex[/127.0.0.1:1234] logged in with entity id 10 at (0, 64, 0)");
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex joined the game");
        instance.ProcessServerLogLine("[12:00:01] [Server thread/INFO]: Alex[/127.0.0.1:1234] logged in with entity id 10 at (0, 64, 0)");
        Assert.Equal(1, instance.GetCurrentPlayerCount());
        instance.ProcessServerLogLine("[12:00:02] [Server thread/INFO]: alex lost connection: Disconnected");
        instance.ProcessServerLogLine("[12:00:02] [Server thread/INFO]: Alex left the game");
        Assert.Equal(0, instance.GetCurrentPlayerCount());
        Assert.Equal(new[] { 1, 0 }, counts);
    }

    [Fact]
    public void LeaveFallbackAndUnknownDisconnectDoNotRemoveOtherPlayers()
    {
        var instance = CreateInstance();
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex joined the game");
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Steve joined the game");
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Nobody lost connection: Disconnected");
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex left the game");
        Assert.Equal(1, instance.GetCurrentPlayerCount());
    }

    [Theory]
    [InlineData(InstanceStatus.Offline)]
    [InlineData(InstanceStatus.Starting)]
    public void LifecycleResetClearsAndPublishesPlayerCount(InstanceStatus status)
    {
        var instance = CreateInstance();
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex joined the game");
        int? published = null;
        instance.OnPlayersChanged += count => published = count;
        instance.SetStatus(status);
        Assert.Equal(0, instance.GetCurrentPlayerCount());
        Assert.Equal(0, published);
        instance.SetStatus(InstanceStatus.Online);
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex joined the game");
        Assert.Equal(1, instance.GetCurrentPlayerCount());
    }

    [Fact]
    public void LateOutputAndChatDoNotCreateGhostPlayers()
    {
        var instance = CreateInstance();
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: <Steve> Alex joined the game");
        Assert.Equal(0, instance.GetCurrentPlayerCount());
        instance.SetStatus(InstanceStatus.Offline);
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Alex joined the game");
        instance.ProcessServerLogLine("[12:00:00] [Server thread/INFO]: Done (1.23s)! For help, type \"help\"");
        Assert.Equal(0, instance.GetCurrentPlayerCount());
        Assert.Equal(InstanceStatus.Offline, instance.Status);
    }
}
