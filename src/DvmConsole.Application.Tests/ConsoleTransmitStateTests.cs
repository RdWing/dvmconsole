// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleTransmitStateTests
{
    [Fact]
    public void UnconfirmedStopRetainsTransmitAndRecordingUntilConfirmation()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "Test", "p25", 100, 0));
        var media = new ConsoleChannelMediaDirectory([(channel, new RadioAliasIndex([]))]);
        var history = new ConsoleCallHistory();
        var recording = new Recording();
        var state = new ConsoleTransmitState(media, history, recording);
        var descriptor = channel.CaptureTransmitDescriptor(new(channel.Runtime.Definition));
        var target = new TransmitTarget(descriptor, new Endpoint());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        state.Starting(channel.Id);
        var call = state.Started(target, 10, now);
        Assert.True(channel.Operator.Snapshot.TransmitEnabled);
        state.ObserveSamples(channel.Id, 10, 42, [1, 2]);
        Assert.Equal(0, recording.Samples);
        channel.Operator.SetRecordingEnabled(true);
        state.ObserveSamples(channel.Id, 10, 42, [1, 2]);
        Assert.Equal(2, recording.Samples);
        state.Stopping(channel.Id);
        state.StopChannel(channel.Id, confirmed: false);
        Assert.True(channel.Operator.Snapshot.TransmitEnabled);
        Assert.True(history.Find(call.Id)!.IsActive);
        Assert.Equal(0, recording.Stops);
        state.StopChannel(channel.Id, confirmed: true);
        Assert.Equal(call.Id, state.CompleteCall(new(channel.Id, 10), now.AddSeconds(1)));
        Assert.False(channel.Operator.Snapshot.TransmitEnabled);
        Assert.False(history.Find(call.Id)!.IsActive);
        Assert.Equal(1, recording.Stops);
        Assert.Null(state.CompleteCall(new(channel.Id, 10), now.AddSeconds(2)));
        Assert.Equal(now.AddSeconds(1), history.Find(call.Id)!.EndedAt);
    }

    [Fact]
    public void BatchStopPublishesChannelsBeforeHistoryAndLeavesUnconfirmedCallOpen()
    {
        var first = new ConsoleChannelState(new ChannelRuntimeDefinition("First", "Test", "p25", 100, 0));
        var second = new ConsoleChannelState(new ChannelRuntimeDefinition("Second", "Test", "p25", 101, 0));
        var media = new ConsoleChannelMediaDirectory([(first, new RadioAliasIndex([])), (second, new RadioAliasIndex([]))]);
        var history = new ConsoleCallHistory();
        var recording = new Recording();
        var state = new ConsoleTransmitState(media, history, recording);
        ChannelId[] ids = [first.Id, second.Id];
        TransmitTarget[] targets = [
            new(first.CaptureTransmitDescriptor(new(first.Runtime.Definition)), new Endpoint()),
            new(second.CaptureTransmitDescriptor(new(second.Runtime.Definition)), new Endpoint())];
        var calls = new List<ConsoleCallHistoryRecord>();
        var events = new List<string>();
        state.Starting(ids);
        state.Started(targets, ids, id => id == first.Id ? 10u : 11u, () => DateTimeOffset.UnixEpoch,
            (target, _, call) =>
            {
                Assert.True(media.State(target.Channel.Id).Operator.Snapshot.TransmitEnabled);
                calls.Add(call);
            });
        TransmitStream[] streams = [new(first.Id, 10), new(second.Id, 11)];
        state.Stopping(streams);
        int count = state.Stopped(ids, streams, new HashSet<ChannelId> { second.Id },
            () => DateTimeOffset.UnixEpoch.AddSeconds(1),
            (id, confirmed) =>
            {
                Assert.Equal(!confirmed, media.State(id).Operator.Snapshot.TransmitEnabled);
                events.Add("channel");
            },
            (stream, call) =>
            {
                Assert.Equal(first.Id, stream.ChannelId);
                Assert.Equal(calls[0].Id, call);
                Assert.False(history.Find(call!.Value)!.IsActive);
                events.Add("history");
            });
        Assert.Equal(1, count);
        Assert.Equal(["channel", "channel", "history"], events);
        Assert.Equal(1, recording.Stops);
        Assert.True(history.Find(calls[1].Id)!.IsActive);
        Assert.Equal(1, state.Stopped([second.Id], [streams[1]], new HashSet<ChannelId>(),
            () => DateTimeOffset.UnixEpoch.AddSeconds(2)));
        Assert.False(history.Find(calls[1].Id)!.IsActive);
        Assert.Equal(2, recording.Stops);
        state.Starting(ids);
        state.StartFailed(ids, id => Assert.False(media.State(id).Operator.Snapshot.TransmitStarting));
    }

    private sealed class Recording : ITransmitRecordingSink
    {
        public int Samples;
        public int Stops;
        public void WriteTransmitSamples(ChannelRecordingDescriptor channel, uint streamId, uint sourceId, ReadOnlySpan<short> samples)
            => Samples += samples.Length;
        public void StopTransmit(ChannelRecordingDescriptor channel) => Stops++;
    }

    private sealed class Endpoint : IRadioTrafficEndpoint
    {
        public string Name => "Test";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public bool IsConnected => true;
        public uint? SourceId => 42;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => TargetAuthorityState.Available;
        public uint CreateStreamId() => 10;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId) { }
    }
}
