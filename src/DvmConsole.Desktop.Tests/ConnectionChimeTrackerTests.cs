// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConnectionChimeTrackerTests
{
    [Fact]
    public void PlaysOneChimeForEachConnectedAndDisconnectedEdge()
    {
        var tracker = new ConnectionChimeTracker();

        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Disconnected));
        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Starting));
        Assert.True(tracker.ShouldPlay("Alpha", RadioConnectionState.Connected));
        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Connected));
        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Stopping));
        Assert.True(tracker.ShouldPlay("Alpha", RadioConnectionState.Disconnected));
        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Disconnected));
    }

    [Fact]
    public void FaultAndFollowingDisconnectedStatusProduceOneDisconnectChime()
    {
        var tracker = new ConnectionChimeTracker();

        Assert.True(tracker.ShouldPlay("Alpha", RadioConnectionState.Connected));
        Assert.True(tracker.ShouldPlay("Alpha", RadioConnectionState.Faulted));
        Assert.False(tracker.ShouldPlay("Alpha", RadioConnectionState.Disconnected));
    }
}
