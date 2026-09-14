// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSnapshotOwnershipTests
{
    [Fact]
    public void SharedSubscriptionsRefreshControlsWithoutPresentationAndDetachOnDisposal()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
        var status = new ConsoleSessionStatus();
        using var snapshots = new ConsoleSnapshotState(new(null, [], [], []),
            [new(channel, new RadioAliasIndex([]), new(channel.Runtime.Definition))],
            _ => new Dictionary<ChannelId, ChannelSnapshotContext>(), status);
        var first = snapshots.Capture();
        int notifications = 0;
        snapshots.Changed += (_, _) => throw new InvalidOperationException("Broken presentation observer");
        snapshots.Changed += (_, _) => notifications++;
        channel.Operator.SetTransmitSelected(true);
        channel.Operator.SetGain(2);
        var controls = snapshots.Capture();
        Assert.True(controls.Channels[channel.Id].TransmitSelected);
        Assert.Equal(2, controls.Channels[channel.Id].Gain);
        Assert.NotSame(first.Channels, controls.Channels);
        status.SetConsole("Listening");
        var updated = snapshots.Capture();
        Assert.Equal("Listening", updated.StatusText);
        Assert.Same(controls.Channels, updated.Channels);
        channel.Meter.Update(1, 2);
        Assert.Same(updated, snapshots.Capture());
        Assert.Equal(3, notifications);
        snapshots.Dispose();
        channel.Operator.SetGain(3);
        status.SetConsole("Retired");
        Assert.Equal(3, notifications);
        Assert.Same(updated, snapshots.Capture());
    }

    [Fact]
    public void PlaybackRejectsStaleStopsAndDelayedLegacyResolution()
    {
        var state = new RecordingPlaybackChannelState();
        var first = RecordingId.New();
        var second = RecordingId.New();
        var old = state.Apply(first, true, null);
        var current = state.Apply(second, true, new ChannelId(new DvmConsole.Operations.ChannelSessionId("System", ChannelProtocol.P25, 100, 0, Guid.NewGuid().ToString())));
        state.ResolveChannel(old.Revision, new ChannelId(new DvmConsole.Operations.ChannelSessionId("System", ChannelProtocol.P25, 100, 0, Guid.NewGuid().ToString())));
        state.Apply(first, false, null);
        Assert.Same(current, state.Snapshot);
        state.Apply(second, false, null);
        state.ResolveChannel(current.Revision, new ChannelId(new DvmConsole.Operations.ChannelSessionId("System", ChannelProtocol.P25, 100, 0, Guid.NewGuid().ToString())));
        Assert.Null(state.Snapshot.Recording);
        Assert.Null(state.Snapshot.Channel);
    }
}
