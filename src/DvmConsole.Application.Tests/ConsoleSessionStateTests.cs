// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSessionStateTests
{
    [Fact]
    public void ReceivePlaybackAdmissionAndMeterRetirementShareStreamOwnership()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
        Assert.False(channel.TryBeginReceivePlayback(42, 10));
        Assert.Equal(0u, channel.Receive.MeterStreamId);
        channel.SetReceiveEnabled(true);
        Assert.False(channel.TryBeginReceivePlayback(42, 0));
        Assert.True(channel.TryBeginReceivePlayback(42, 10));
        Assert.Equal(10u, channel.Receive.MeterStreamId);
        Assert.False(channel.TryBeginReceivePlayback(99, 20));
        channel.EndReceivePlayback(20);
        Assert.Equal(10u, channel.Receive.MeterStreamId);
        Assert.Equal(42u, channel.Receive.Playback!.SourceId);
        channel.EndReceivePlayback(10);
        Assert.Null(channel.Receive.Playback);
        Assert.Equal(0u, channel.Receive.MeterStreamId);
        channel.SetAudioSuspended(true);
        Assert.False(channel.TryBeginReceivePlayback(99, 20));
        channel.SetAudioSuspended(false);
        Assert.True(channel.TryBeginReceivePlayback(99, 20));
        channel.MarkReceiveMeter(10, ended: true);
        Assert.Equal(20u, channel.Receive.MeterStreamId);
        channel.MarkReceiveMeter(20, ended: true);
        Assert.Equal(0u, channel.Receive.MeterStreamId);
    }

    [Fact]
    public void MediaDirectoryReadsLiveControlStateAndKeepsFrozenAliases()
    {
        var alias = new RadioAlias { Rid = 42, Alias = "Original" };
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
        var media = new ConsoleChannelMediaDirectory([(channel, new RadioAliasIndex([alias]))]);
        alias.Alias = "Edited later";
        channel.Operator.SetGain(0.25);
        channel.Operator.SetBalance(-0.5);
        channel.Operator.SetRecordingEnabled(true);
        channel.Operator.SetTransmitEncrypted(true);
        channel.RecordingSubscribers.Replace([43]);
        channel.Runtime.MarkReceiving(42, 10);
        channel.Receive.TryBeginPlayback(99, 20);

        Assert.Same(channel, media.State(channel.Id));
        Assert.Equal(0.25, media.Gain(channel.Id));
        Assert.Equal(-0.5, media.Balance(channel.Id));
        Assert.Equal("Original", media.SubscriberAlias(channel.Id, 42));
        Assert.Equal(string.Empty, media.SubscriberAlias(channel.Id, 99));
        Assert.True(media.ShouldRecord(channel.Id, 42));
        Assert.False(media.ShouldRecord(channel.Id, 43));
        var descriptor = media.DescribeRecording(channel.Id);
        Assert.True(descriptor.RecordingEnabled);
        Assert.True(descriptor.TransmitEncrypted);
        Assert.Equal(42u, descriptor.ActiveSourceId);
        Assert.Equal(10u, descriptor.ActiveStreamId);
        var presented = channel.CaptureRecordingDescriptor(presentedReceive: true);
        Assert.Equal(99u, presented.ActiveSourceId);
        Assert.Equal(20u, presented.ActiveStreamId);
        channel.Operator.SetGain(0.75);
        channel.RecordingSubscribers.Replace([]);
        Assert.Equal(0.75, media.Gain(channel.Id));
        Assert.True(media.ShouldRecord(channel.Id, 43));
    }

    [Fact]
    public void SuspensionClearsPlaybackWithoutChangingReceiveSelectionOrRestoringAnOldCall()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
        channel.SetReceiveEnabled(true);
        channel.Receive.BeginMeter(10);
        channel.Receive.TryBeginPlayback(42, 10);
        Assert.True(channel.SetAudioSuspended(true));
        Assert.True(channel.Operator.Snapshot.AudioEnabled);
        Assert.True(channel.Operator.Snapshot.AudioSuspended);
        Assert.Null(channel.Receive.Playback);
        Assert.Equal(0, channel.Receive.MeterStreamId);
        Assert.False(channel.SetAudioSuspended(true));
        Assert.True(channel.SetAudioSuspended(false));
        Assert.True(channel.Operator.Snapshot.AudioEnabled);
        Assert.Null(channel.Receive.Playback);
    }

    [Fact]
    public void PreparedSnapshotMetadataSurvivesConfigurationEdits()
    {
        var alias = new RadioAlias { Rid = 42, Alias = "Dispatch radio" };
        var definition = new ChannelConfiguration { Name = "First", System = "System", Mode = "p25", Tgid = "100" };
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "System", Identity = "Console", Address = "127.0.0.1",
                Port = 62031, PeerId = 1, Rid = "1001", RidAlias = [alias] }],
            Zones = [new ZoneConfiguration { Name = "Dispatch", Channels = [definition] }]
        };
        ConsoleSessionState session = ConsoleSessionState.Create(configuration);
        alias.Alias = "Changed in editor";
        definition.Name = "Changed channel";
        var snapshot = Assert.Single(session.CreateSnapshotChannels());
        Assert.Same(Assert.Single(session.Channels.Values), snapshot.State);
        Assert.Same(session.Aliases[SystemId.FromName("System")], snapshot.Aliases);
        Assert.Equal("Dispatch radio", snapshot.Aliases.Find(42));
        Assert.Equal("First", snapshot.State.Runtime.Definition.Name);
        snapshot.State.SetReceiveEnabled(true);
        snapshot.State.Runtime.MarkReceiving(42, 10);
        Assert.Contains("Dispatch radio", ConsoleChannelSnapshotProjector.StateText(snapshot.State, snapshot.Aliases));
    }

    [Fact]
    public void TransmitStateAndStreamLifetimeDoNotRequireADesktopObserver()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
        channel.Operator.SetTransmitTransition(starting: true, stopping: false);
        Assert.True(channel.SetTransmitEnabled(true, 77));
        Assert.True(channel.Operator.Snapshot.TransmitEnabled);
        Assert.False(channel.Operator.Snapshot.TransmitStarting);
        Assert.Equal(ChannelRuntimeState.Transmitting, channel.Runtime.State);
        Assert.Equal(77u, channel.Runtime.StreamId);
        Assert.False(channel.SetTransmitEnabled(true, 88));
        Assert.Equal(88u, channel.Runtime.StreamId);
        channel.Operator.SetTransmitTransition(starting: false, stopping: true);
        Assert.True(channel.SetTransmitEnabled(false));
        Assert.False(channel.Operator.Snapshot.TransmitEnabled);
        Assert.False(channel.Operator.Snapshot.TransmitStopping);
        Assert.Null(channel.Runtime.StreamId);
        Assert.Equal(ChannelRuntimeState.Idle, channel.Runtime.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.SetTransmitEnabled(true));
        Assert.False(channel.Operator.Snapshot.TransmitEnabled);
    }

    [Fact]
    public void DisablingReceiveClearsPlaybackAndMeterWithoutADesktopObserver()
    {
        ConsoleSessionState session = ConsoleSessionState.Create(new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "System", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
                [new ChannelConfiguration { Name = "First", System = "System", Mode = "p25", Tgid = "100" }] }]
        });
        ConsoleChannelState channel = Assert.Single(session.Channels.Values);
        Assert.True(channel.SetReceiveEnabled(true));
        channel.Receive.BeginMeter(10);
        channel.Receive.TryBeginPlayback(42, 10);
        Assert.True(channel.SetReceiveEnabled(false));
        Assert.False(channel.Operator.Snapshot.AudioEnabled);
        Assert.Null(channel.Receive.Playback);
        Assert.Equal(0, channel.Receive.MeterStreamId);
        Assert.False(channel.SetReceiveEnabled(false));
    }

    [Fact]
    public void RuntimeInvalidatesEquivalentChannelCopiesWithoutDesktopObservers()
    {
        ConsoleSessionState session = ConsoleSessionState.Create(new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "System", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
            [new ChannelConfiguration { Name = "First", System = "System", Mode = "p25", Tgid = "100" },
             new ChannelConfiguration { Name = "Second", System = "System", Mode = "p25", Tgid = "100" }] }]
        });
        SystemId systemId = SystemId.FromName("System");
        Assert.Single(session.KeyRequests);
        session.KeyRequests[systemId].ObserveResponse(1, 2);
        Assert.True(session.KeyRequests[systemId].HasResponse(1, 2));
        var projected = new HashSet<ChannelId>();
        using var snapshots = new ConsoleSnapshotState(session.Topology, session.CreateSnapshotChannels(),
            ids => { projected.UnionWith(ids); return new Dictionary<ChannelId, ChannelSnapshotContext>(); }, session.Status);
        snapshots.Capture();
        projected.Clear();
        ConsoleChannelState channel = session.Channels.Values.First();
        channel.Runtime.MarkReceiving(42, 10);
        snapshots.Capture();
        Assert.True(projected.SetEquals(session.Channels.Keys));
        projected.Clear();
        channel.Runtime.MarkReceiving(42, 10);
        snapshots.Capture();
        Assert.Empty(projected);
        channel.Receive.TryBeginPlayback(42, 10);
        snapshots.Capture();
        Assert.True(projected.SetEquals(session.Channels.Keys));
        projected.Clear();
        channel.SetAuthority(TargetAuthorityState.Available);
        snapshots.Capture();
        Assert.Equal([channel.Id], projected);
    }
}
