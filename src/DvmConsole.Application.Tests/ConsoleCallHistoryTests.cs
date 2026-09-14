// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleCallHistoryTests
{
    [Theory]
    [InlineData("p25", 0x84, 0x12, true)]
    [InlineData("p25", 0x84, 0x12, false)]
    [InlineData("dmr", 0x05, 12, true)]
    [InlineData("dmr", 0x05, 12, false)]
    [InlineData("nxdn", 0x03, 12, true)]
    [InlineData("nxdn", 0x03, 12, false)]
    public void TransmitMetadataUsesAdmittedEncryptionDespiteLaterOperatorChange(string protocol, byte algorithm, ushort keyId, bool secure)
    {
        var definition = new ChannelRuntimeDefinition("Dispatch", "System", protocol, 100, 0,
            encryptionAlgorithm: "aes", encryptionKeyId: "12", selectableEncryption: true);
        var state = new ConsoleChannelState(definition);
        state.Operator.SetTransmitEncrypted(secure);
        TransmitChannelDescriptor admitted = state.CaptureTransmitDescriptor(new ChannelConfigurationAccess(definition));
        state.Operator.SetTransmitEncrypted(!secure);

        var history = new ConsoleCallHistory();
        var record = history.BeginTransmit(DateTimeOffset.UnixEpoch, new(admitted, new HistoryEndpoint()), 77);
        Assert.Equal(secure ? RecordingEncryptionDescriptor.Secure(algorithm, keyId)
            : RecordingEncryptionDescriptor.Clear, record.Encryption);
        Assert.Equal(state.Id, record.ChannelId);
        Assert.Equal(42u, record.SourceId);
        Assert.Equal(100u, record.DestinationId);
        Assert.Equal("Console", record.Caller);
        Assert.Equal(EncryptionProtocolLabels.ParseProtocol(protocol), record.Protocol);
        Assert.Same(record, Assert.Single(history.Snapshot));
    }

    private sealed class HistoryEndpoint : IRadioTrafficEndpoint
    {
        public string Name => "System";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public bool IsConnected => true;
        public uint? SourceId => 42;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot) => default;
        public uint CreateStreamId() => throw new NotSupportedException();
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
            => throw new NotSupportedException("History projection must not transmit.");
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(77u, true)]
    public void StartsTransmitWithStableIdentityAndCanonicalEncryption(uint streamId, bool secure)
    {
        var history = new ConsoleCallHistory();
        ConsoleCallHistoryRecord record = history.BeginTransmit(
            DateTimeOffset.UnixEpoch, "System", "Dispatch", 42, 100,
            RadioMediaProtocol.P25, streamId, " Console ", secure, 0x84, 12);

        Assert.Same(record, Assert.Single(history.Snapshot));
        Assert.Equal("Console", record.Caller);
        Assert.Equal(ConsoleCallDirection.Transmit, record.Direction);
        Assert.Equal(secure ? RecordingEncryptionDescriptor.Secure(0x84, 12)
            : RecordingEncryptionDescriptor.Clear, record.Encryption);
        Assert.Equal(streamId == 0 ? Array.Empty<uint>() : new[] { streamId }, record.StreamIds);
        Assert.Null(history.FindActiveReceive("System", RadioMediaProtocol.P25, streamId));
        Assert.Equal(record.Id, history.FindActiveTransmit("System", RadioMediaProtocol.P25, streamId));
        Assert.True(history.Complete(record.Id, record.StartedAt.AddSeconds(1)));
        Assert.True(record.IsActive); // Previously published snapshots remain unchanged.
        Assert.False(history.Find(record.Id)!.IsActive);
    }

    [Fact]
    public void TracksCallLifecycleByStableIdAndBoundsTheNewestEntries()
    {
        var history = new ConsoleCallHistory(maximumEntries: 2);
        ConsoleCallHistoryRecord first = CreateCall(1, DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        ConsoleCallHistoryRecord second = CreateCall(2, first.StartedAt.AddSeconds(1));
        ConsoleCallHistoryRecord third = CreateCall(3, first.StartedAt.AddSeconds(2));

        history.Add(first);
        history.Add(second);
        history.Add(third);

        Assert.Equal([third.Id, second.Id], history.Snapshot.Select(call => call.Id));
        Assert.Null(history.FindActiveReceive(
            first.SystemName,
            first.Protocol,
            first.PrimaryStreamId));
        Assert.Equal(second.Id, history.FindActiveReceive(
            second.SystemName,
            second.Protocol,
            second.PrimaryStreamId));
    }

    [Fact]
    public void UpdatesStreamsEncryptionAndCompletionWithoutPresentationObjects()
    {
        var history = new ConsoleCallHistory();
        ConsoleCallHistoryRecord call = CreateCall(10, DateTimeOffset.Parse("2026-08-30T12:00:00Z"));
        history.Add(call);

        Assert.True(history.ObserveStream(call.Id, 11));
        Assert.True(history.UpdateEncryption(
            call.Id,
            RecordingEncryptionDescriptor.Secure(0x80, 0x1234)));
        Assert.True(history.Complete(call.Id, call.StartedAt.AddSeconds(3)));

        ConsoleCallHistoryRecord updated = Assert.Single(history.Snapshot);
        Assert.Equal(new uint[] { 10, 11 }, updated.StreamIds);
        Assert.True(updated.Encryption.IsSecure);
        Assert.Equal((ushort)0x1234, updated.Encryption.KeyId);
        Assert.Equal(call.StartedAt.AddSeconds(3), updated.EndedAt);
        Assert.False(updated.IsActive);
    }

    [Theory]
    [InlineData(49, false, true)]
    [InlineData(50, false, false)]
    [InlineData(1, true, false)]
    [InlineData(-1, false, true)]
    public void CompletesShortCallsWithoutDiscardingRecordingBackedHistory(int milliseconds, bool recorded, bool removed)
    {
        var history = new ConsoleCallHistory();
        ConsoleCallHistoryRecord call = CreateCall(10, DateTimeOffset.UnixEpoch);
        history.Add(call);
        history.SetRecordingAttached(call.Id, recorded);
        ConsoleCallCompletion completed = Assert.IsType<ConsoleCallCompletion>(history.CompleteReceive(
            call.SystemName, call.Protocol, call.PrimaryStreamId, call.StartedAt.AddMilliseconds(milliseconds)));
        Assert.Equal(removed, completed.Removed);
        Assert.Equal(call.StartedAt.AddMilliseconds(Math.Max(0, milliseconds)), completed.Record.EndedAt);
        Assert.Equal(removed, history.Find(call.Id) is null);
        Assert.Null(history.CompleteReceive(call.SystemName, call.Protocol, call.PrimaryStreamId,
            call.StartedAt.AddSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingBindingAttachesOnlyRetainedCallsAndDetachesOnRetirement(bool composed)
    {
        var history = new ConsoleCallHistory();
        var call = CreateCall(10, DateTimeOffset.UnixEpoch);
        history.Add(call);
        var source = new AttachmentSource();
        int notifications = 0;
        using var runtime = new ConsoleRecordingRuntime([]);
        IDisposable attachment;
        if (composed)
        {
            runtime.Initialize(new Dictionary<ChannelId, ConsoleChannelState>(), new ReceiveCallEpisodeTracker(),
                history, source, _ => throw new InvalidOperationException("No channel samples in this fixture."),
                () => notifications++);
            attachment = runtime;
        }
        else attachment = new RecordingHistoryBinding(source, history, () => notifications++);
        using var binding = attachment;
        source.Publish(RecordingCallIdentity.FromCall(call));
        Assert.True(history.Find(call.Id)!.HasRecording);
        Assert.Equal(1, notifications);
        history.Clear();
        source.Publish(RecordingCallIdentity.FromCall(call));
        Assert.Empty(history.Snapshot);
        Assert.Equal(1, notifications);
        history.Add(call);
        binding.Dispose();
        history.SetRecordingAttached(call.Id, false);
        source.Publish(RecordingCallIdentity.FromCall(call));
        Assert.False(history.Find(call.Id)!.HasRecording);
        Assert.Equal(1, notifications);
        history.Clear();
        source.Publish(RecordingCallIdentity.FromCall(call));
        Assert.Empty(history.Snapshot);
    }

    private sealed class AttachmentSource : IRecordingCallAttachmentSource, IReceiveRecordingSink
    {
        public event Action<RecordingCallIdentity>? RecordingAttached;
        public void Publish(RecordingCallIdentity identity) => RecordingAttached?.Invoke(identity);
        public void WriteEpisodeSamples(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
            uint sourceId, ReadOnlyMemory<short> samples, long? receiveEpisodeId = null)
        { }
        public void ObserveEpisodeTraffic(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
            IRadioMediaFrame traffic, long? receiveEpisodeId = null)
        { }
        public void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId) { }
        public void StopChannel(ChannelRecordingDescriptor channel) { }
    }

    [Fact]
    public void RecordingAttachmentPublishesImmutableStateAndCannotReviveRetiredCalls()
    {
        var history = new ConsoleCallHistory();
        var call = CreateCall(10, DateTimeOffset.UnixEpoch);
        history.Add(call);
        Assert.Equal(call.Id, history.AttachRecording(RecordingCallIdentity.FromCall(call)));
        Assert.False(call.HasRecording);
        Assert.True(history.Find(call.Id)!.HasRecording);
        Assert.False(history.SetRecordingAttached(call.Id, true));
        Assert.True(history.SetRecordingAttached(call.Id, false));
        Assert.False(history.Find(call.Id)!.HasRecording);
        history.Clear();
        Assert.False(history.SetRecordingAttached(call.Id, true));
        Assert.Empty(history.Snapshot);
    }

    private static ConsoleCallHistoryRecord CreateCall(uint streamId, DateTimeOffset startedAt)
        => new(
            CallId.New(),
            startedAt,
            null,
            SystemId.FromName("North"),
            "North",
            null,
            "Dispatch",
            RadioMediaProtocol.P25,
            1001,
            2001,
            streamId,
            [streamId],
            null,
            "Unit 1001",
            ConsoleCallDirection.Receive,
            RecordingEncryptionDescriptor.Clear,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}
