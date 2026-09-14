// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceiveCallHistoryCoordinatorTests
{
    [Fact]
    public void LateEntryCreatesOneAuthoritativeCallAndUnknownPacketsRetainSecureMetadata()
    {
        var history = new ConsoleCallHistory();
        var presentation = new Presentation();
        var (system, channel) = CreateChannel();
        var channels = new ConsoleChannelMediaDirectory([(channel,
            new RadioAliasIndex([new RadioAlias { Rid = 42, Alias = " Caller 42 " }]))]);
        var coordinator = new ReceiveCallHistoryCoordinator(history, channels, presentation);
        var now = DateTimeOffset.Parse("2026-09-09T12:00:00Z");
        var decision = new ReceiveIngressDecision(new Frame(), now, 1,
            ReceiveIngressRoutingDecision.Empty, null, null);
        var continued = new ReceiveStreamDecision(ReceiveStreamTransition.Continued);
        var secure = EncryptionSnapshot.FromStored(CallRecordingEncryptionState.Secure, 0x84, 12);

        Assert.True(coordinator.Observe(system, channel, decision, continued, secure));
        ConsoleCallHistoryRecord call = Assert.Single(history.Snapshot);
        Assert.Equal(channel.Id, call.ChannelId);
        Assert.Equal("Caller 42", call.Caller);
        Assert.Equal((ushort)12, call.Encryption.KeyId);
        Assert.Same(call, Assert.Single(presentation.Records));
        Assert.False(coordinator.Observe(system, channel, decision, continued, EncryptionSnapshot.Unknown));
        Assert.False(coordinator.Observe(system, channel, decision, continued, secure));
        Assert.Single(history.Snapshot);
        Assert.Single(presentation.Records);
        Assert.True(history.Find(call.Id)!.Encryption.IsSecure);
    }

    [Theory]
    [InlineData(ReceiveStreamTransition.None)]
    [InlineData(ReceiveStreamTransition.IgnoredLate)]
    [InlineData(ReceiveStreamTransition.TerminationPending)]
    public void NonStartingTransitionCannotInventAHistoryCall(ReceiveStreamTransition transition)
    {
        var history = new ConsoleCallHistory();
        var presentation = new Presentation();
        var (system, channel) = CreateChannel();
        var channels = new ConsoleChannelMediaDirectory([(channel,
            new RadioAliasIndex([new RadioAlias { Rid = 42, Alias = " Caller 42 " }]))]);
        var coordinator = new ReceiveCallHistoryCoordinator(history, channels, presentation);
        var decision = new ReceiveIngressDecision(new Frame(), DateTimeOffset.UnixEpoch, 1,
            ReceiveIngressRoutingDecision.Empty, null, null);
        Assert.False(coordinator.Observe(system, channel, decision, new(transition), EncryptionSnapshot.Unknown));
        Assert.Empty(history.Snapshot);
        Assert.Empty(presentation.Records);
    }

    private static (ReceiveIngressSystem, ConsoleChannelState) CreateChannel()
    {
        var state = ConsoleSessionState.Create(new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "System", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new ZoneConfiguration { Name = "Dispatch", Channels =
                [new ChannelConfiguration { Name = "Dispatch", System = "System", Mode = "p25", Tgid = "100" }] }]
        });
        var channel = Assert.Single(state.Channels.Values);
        channel.Runtime.MarkReceiving(42, 10);
        return (new(SystemId.FromName("System"), "System", [channel]), channel);
    }

    private sealed class Presentation : IReceiveCallHistoryPresentation
    {
        public List<ConsoleCallHistoryRecord> Records { get; } = [];
        public string DescribeSignalQuality(IRadioMediaFrame traffic) => string.Empty;
        public void Project(ConsoleCallHistoryRecord record) => Records.Add(record);
        public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) { }
    }

    private sealed class Frame : IRadioMediaFrame
    {
        public RadioMediaProtocol Protocol => RadioMediaProtocol.P25;
        public uint PeerId => 1;
        public uint SourceId => 42;
        public uint DestinationId => 100;
        public byte? Slot => null;
        public string CallType => "GROUP";
        public string FrameType => "VOICE";
        public string Subtype => "LDU1";
        public ushort PacketSequence => 1;
        public uint StreamId => 10;
        public byte[] Payload => [];
    }
}
