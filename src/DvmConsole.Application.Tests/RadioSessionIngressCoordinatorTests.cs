using DvmConsole.Core.Runtime;
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
    public void DisposeDetachesEveryRadioSession()
    {
        var session = new TestRadioSession("North");
        var coordinator = new RadioSessionIngressCoordinator([session]);
        int trafficCount = 0;
        coordinator.TrafficReceived += (_, _) => trafficCount++;

        coordinator.Dispose();
        session.PublishTraffic(new RadioTrafficRecord(
            session.SystemId,
            [],
            new TestRadioFrame(),
            DateTimeOffset.UnixEpoch));

        Assert.Equal(0, trafficCount);
    }

    private sealed class TestRadioSession : IRadioSession
    {
        public TestRadioSession(string name)
        {
            Name = name;
            SystemId = SystemId.FromName(name);
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
            ReadOnlySpan<byte> payload,
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
