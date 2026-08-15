using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConnectionChimeTrackerTests
{
    [Fact]
    public void PlaysOneChimeForEachConnectedAndDisconnectedEdge()
    {
        var tracker = new ConnectionChimeTracker();

        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Disconnected));
        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Starting));
        Assert.True(tracker.ShouldPlay("Alpha", FneConnectionState.Connected));
        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Connected));
        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Stopping));
        Assert.True(tracker.ShouldPlay("Alpha", FneConnectionState.Disconnected));
        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Disconnected));
    }

    [Fact]
    public void FaultAndFollowingDisconnectedStatusProduceOneDisconnectChime()
    {
        var tracker = new ConnectionChimeTracker();

        Assert.True(tracker.ShouldPlay("Alpha", FneConnectionState.Connected));
        Assert.True(tracker.ShouldPlay("Alpha", FneConnectionState.Faulted));
        Assert.False(tracker.ShouldPlay("Alpha", FneConnectionState.Disconnected));
    }
}
