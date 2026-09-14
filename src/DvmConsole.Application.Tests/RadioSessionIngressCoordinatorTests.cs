// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RadioSessionIngressCoordinatorTests
{
    [Fact]
    public void ForwardsValidatedTrafficAndAuthorityRecords()
    {
        var session = new TestRadioSession("North");
        using var coordinator = new RadioSessionIngressCoordinator([session]);
        RadioTrafficRecord? observedTraffic = null;
        TalkgroupAuthorityRecord? observedAuthority = null;
        coordinator.TrafficReceived += (_, traffic) => observedTraffic = traffic;
        coordinator.AuthorityChanged += (_, authority) => observedAuthority = authority;
        ChannelId channelId = CreateChannelId();
        var frame = new TestRadioFrame();

        session.PublishTraffic(new RadioTrafficRecord(
            session.SystemId,
            [channelId],
            frame,
            DateTimeOffset.UnixEpoch,
            BoundaryTimestamp: 41,
            TransportIngressTimestamp: 17));
        session.PublishAuthority(new TalkgroupAuthorityRecord(
            session.SystemId,
            [new TalkgroupAuthorityChannelRecord(
                channelId,
                TargetAuthorityState.Unavailable,
                "not authorized")],
            DateTimeOffset.UnixEpoch));

        Assert.NotNull(observedTraffic);
        Assert.Same(frame, observedTraffic.Traffic);
        Assert.Equal([channelId], observedTraffic.CandidateChannels);
        Assert.Equal(41, observedTraffic.BoundaryTimestamp);
        Assert.Equal(17, observedTraffic.TransportIngressTimestamp);
        TalkgroupAuthorityChannelRecord observedChannel = Assert.Single(observedAuthority!.Channels);
        Assert.Equal(channelId, observedChannel.ChannelId);
        Assert.Equal(TargetAuthorityState.Unavailable, observedChannel.State);
    }

    [Fact]
    public void RejectsRecordsWhoseStableSystemIdDoesNotMatchTheirSender()
    {
        var session = new TestRadioSession("North");
        using var coordinator = new RadioSessionIngressCoordinator([session]);
        int trafficCount = 0;
        int authorityCount = 0;
        coordinator.TrafficReceived += (_, _) => trafficCount++;
        coordinator.AuthorityChanged += (_, _) => authorityCount++;
        SystemId wrongSystem = SystemId.FromName("South");

        session.PublishTraffic(new RadioTrafficRecord(
            wrongSystem,
            [],
            new TestRadioFrame(),
            DateTimeOffset.UnixEpoch));
        session.PublishAuthority(new TalkgroupAuthorityRecord(
            wrongSystem,
            [new TalkgroupAuthorityChannelRecord(
                CreateChannelId(),
                TargetAuthorityState.Available,
                null)],
            DateTimeOffset.UnixEpoch));

        Assert.Equal(0, trafficCount);
        Assert.Equal(0, authorityCount);
    }

    [Fact]
    public async Task SessionRetirementDetachesEveryRadioSession()
    {
        var session = new TestRadioSession("North");
        await using var services = new ConsoleSessionServices();
        var runtime = new ConsoleOperationalRuntime(services, []);
        runtime.BindRadios([session]);
        runtime.InitializeRadioIngress(services.Connection, "radio-ingress");
        var coordinator = runtime.RadioIngress;
        int trafficCount = 0;
        coordinator.TrafficReceived += (_, _) => trafficCount++;

        await services.DisposeAsync();
        session.PublishTraffic(new RadioTrafficRecord(
            session.SystemId,
            [],
            new TestRadioFrame(),
            DateTimeOffset.UnixEpoch));

        Assert.Equal(0, trafficCount);
    }

    [Fact]
    public void ConnectionNotificationsRequireRegisteredSenderAndMatchingIdentity()
    {
        var radio = new TestRadioSession("North");
        var impostor = new TestRadioSession("North");
        using var coordinator = new RadioSessionIngressCoordinator([radio]);
        var observed = new List<RadioConnectionSnapshot>();
        coordinator.ConnectionChanged += (sender, snapshot) =>
        {
            Assert.Same(radio, sender);
            observed.Add(snapshot);
        };
        radio.PublishConnection(impostor);
        radio.ConnectionState = radio.ConnectionState with { SystemId = SystemId.FromName("South") };
        radio.PublishConnection();
        Assert.Empty(observed);
        radio.ConnectionState = radio.ConnectionState with { SystemId = radio.SystemId };
        radio.PublishConnection();
        Assert.Same(radio.ConnectionState, Assert.Single(observed));
        coordinator.Dispose();
        radio.PublishConnection();
        Assert.Single(observed);
    }

    [Fact]
    public void KeyResponsesRejectForeignSendersAndRetiredSubscriptions()
    {
        var radio = new TestRadioSession("North");
        var impostor = new TestRadioSession("North");
        using var coordinator = new RadioSessionIngressCoordinator([radio]);
        var observed = new List<RadioP25KeyResponse>();
        coordinator.P25KeyReceived += (_, response) => observed.Add(response);
        var key = new RadioP25KeyResponse(radio.SystemId, 0x84, 1, new byte[32]);
        radio.PublishKey(key, impostor);
        radio.PublishKey(key with { SystemId = SystemId.FromName("South") });
        Assert.Empty(observed);
        radio.PublishKey(key);
        Assert.Same(key, Assert.Single(observed));
        coordinator.Dispose();
        radio.PublishKey(key);
        Assert.Single(observed);
    }

    [Fact]
    public void AcknowledgementsAndLogsRequireOwnedRadioAndStopAfterDisposal()
    {
        var radio = new TestRadioSession("North");
        var impostor = new TestRadioSession("North");
        using var coordinator = new RadioSessionIngressCoordinator([radio]);
        var acknowledgements = new List<ConsoleSubscriberAcknowledgement>();
        var logs = new List<DebugLogEntry>();
        coordinator.SubscriberAcknowledged += (_, response) => acknowledgements.Add(response);
        coordinator.LogPublished += (_, entry) => logs.Add(entry);
        var response = new ConsoleSubscriberAcknowledgement(radio.SystemId, ConsoleSubscriberCommand.RadioCheck, 123);
        var entry = new DebugLogEntry(DateTimeOffset.UnixEpoch, "Transport", DebugLogSeverity.Info, "Ready");
        radio.PublishAcknowledgement(response, impostor);
        radio.PublishAcknowledgement(response with { System = SystemId.FromName("South") });
        radio.PublishLog(entry, impostor);
        Assert.Empty(acknowledgements);
        Assert.Empty(logs);
        radio.PublishAcknowledgement(response);
        radio.PublishLog(entry);
        Assert.Same(response, Assert.Single(acknowledgements));
        Assert.Same(entry, Assert.Single(logs));
        coordinator.Dispose();
        radio.PublishAcknowledgement(response);
        radio.PublishLog(entry);
        Assert.Single(acknowledgements);
        Assert.Single(logs);
    }

    private sealed class TestRadioSession : IRadioSession, IRadioConnectionStateNotifications, IRadioP25KeyEndpoint, IRadioSubscriberAcknowledgementSource, IRadioLogSource
    {
        public TestRadioSession(string name)
        {
            Name = name;
            SystemId = SystemId.FromName(name);
            ConnectionState = new(SystemId, name, RadioConnectionState.Connected, "Ready", DateTimeOffset.UnixEpoch);
        }

        public SystemId SystemId { get; }
        public string Name { get; }
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public bool IsConnected => true;
        public bool IsConnectionActive => true;
        public uint? SourceId => null;

        public event EventHandler<RadioTrafficRecord>? TrafficReceived;
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;

        public event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;
        public event EventHandler<DebugLogEntry>? LogPublished;
        public void PublishAcknowledgement(ConsoleSubscriberAcknowledgement response, object? sender = null)
            => SubscriberAcknowledged?.Invoke(sender ?? this, response);
        public void PublishLog(DebugLogEntry entry, object? sender = null)
            => LogPublished?.Invoke(sender ?? this, entry);

        public event EventHandler<RadioP25KeyResponse>? P25KeyReceived;
        public void PublishKey(RadioP25KeyResponse response, object? sender = null)
            => P25KeyReceived?.Invoke(sender ?? this, response);
        public void RequestP25Key(byte algorithm, ushort key) { }

        public RadioConnectionSnapshot ConnectionState { get; set; }
        public event EventHandler? ConnectionStateChanged;
        public void PublishConnection(object? sender = null)
            => ConnectionStateChanged?.Invoke(sender ?? this, EventArgs.Empty);

        public void PublishTraffic(RadioTrafficRecord traffic)
            => TrafficReceived?.Invoke(this, traffic);

        public void PublishAuthority(TalkgroupAuthorityRecord authority)
            => AuthorityChanged?.Invoke(this, authority);

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        public TargetAuthorityState GetTargetAuthority(
            RadioMediaProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => TargetAuthorityState.Available;

        public uint CreateStreamId() => 1;

        public void SendTraffic(
            RadioMediaProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint streamId)
        {
        }
    }

    private static ChannelId CreateChannelId()
        => new(new ChannelSessionId(
            "North",
            ChannelProtocol.P25,
            destinationId: 3100,
            slot: 0,
            instanceKey: "dispatch"));

    private sealed class TestRadioFrame : IRadioMediaFrame
    {
        public RadioMediaProtocol Protocol => RadioMediaProtocol.P25;
        public uint PeerId => 1;
        public uint SourceId => 2;
        public uint DestinationId => 3;
        public byte? Slot => null;
        public string CallType => "group";
        public string FrameType => "voice";
        public string Subtype => "test";
        public ushort PacketSequence => 4;
        public uint StreamId => 5;
        public byte[] Payload => [];
    }
}
