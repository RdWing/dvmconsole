// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSnapshotStateTests
{
    [Fact]
    public void IncrementalCaptureReadsApplicationStateAndPreservesUnchangedEntries()
    {
        var first = Channel("First");
        var second = Channel("Second");
        var captured = new List<ChannelId[]>();
        string status = "Ready";
        var snapshots = new ConsoleSnapshotState(ConsoleTopologySnapshot.Empty, [first, second], ids =>
        {
            captured.Add(ids.ToArray());
            return new Dictionary<ChannelId, ChannelSnapshotContext>();
        }, () => status);
        ConsoleRuntimeSnapshot initial = snapshots.Capture();
        Assert.Same(initial, snapshots.Capture());
        first.State.Operator.SetAudioEnabled(true);
        snapshots.Invalidate(first.State.Id);
        ConsoleSnapshotUpdate update = snapshots.CaptureUpdate(initial);
        Assert.True(update.Snapshot.Channels[first.State.Id].ReceiveEnabled);
        Assert.False(initial.Channels[first.State.Id].ReceiveEnabled);
        Assert.Same(initial.Channels[second.State.Id], update.Snapshot.Channels[second.State.Id]);
        Assert.Equal(new[] { first.State.Id }, update.ChangedChannels);
        Assert.Equal(new[] { first.State.Id }, captured[1]);
        status = "Connected";
        snapshots.Invalidate(includeChannels: false);
        ConsoleSnapshotUpdate statusUpdate = snapshots.CaptureUpdate(update.Snapshot);
        Assert.Same(update.Snapshot.Channels, statusUpdate.Snapshot.Channels);
        Assert.Empty(statusUpdate.ChangedChannels!);
        Assert.Empty(captured[2]);
        Assert.Equal("Connected", statusUpdate.Snapshot.StatusText);
    }

    [Fact]
    public void OlderBaselineRequestsFullDiffAndFailedCaptureCanBeRetried()
    {
        var channel = Channel("First");
        bool fail = false;
        var snapshots = new ConsoleSnapshotState(ConsoleTopologySnapshot.Empty, [channel], _ =>
            fail ? throw new IOException("Context temporarily unavailable") :
                new Dictionary<ChannelId, ChannelSnapshotContext>(), () => "Ready");
        ConsoleRuntimeSnapshot initial = snapshots.Capture();
        channel.State.Operator.SetAudioEnabled(true);
        snapshots.Invalidate(channel.State.Id);
        fail = true;
        Assert.Throws<IOException>(() => snapshots.Capture());
        fail = false;
        ConsoleRuntimeSnapshot next = snapshots.Capture();
        Assert.True(next.Channels[channel.State.Id].ReceiveEnabled);
        channel.State.Operator.SetRecordingEnabled(true);
        snapshots.Invalidate(channel.State.Id);
        Assert.Null(snapshots.CaptureUpdate(initial).ChangedChannels);
    }

    private static ConsoleSnapshotChannel Channel(string name)
    {
        var state = new ConsoleChannelState(new ChannelRuntimeDefinition(name, "System", "p25", 100, 0));
        return new(state, new RadioAliasIndex(null), new ChannelConfigurationAccess(state.Runtime.Definition));
    }
}
