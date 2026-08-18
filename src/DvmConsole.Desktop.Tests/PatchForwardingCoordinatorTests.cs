using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchForwardingCoordinatorTests
{
    [Fact]
    public void ForwardsAnalogAudioAndEndsTheTargetLifecycle()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = Create("Source", 100, 1001);
        (ChannelViewModel target, FakeEndpoint targetSystem) = Create("Target", 200, 2002);
        using var coordinator = new PatchForwardingCoordinator([sourceSystem, targetSystem]);
        coordinator.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });

        ObserveVoice(coordinator, source, 77, 7001);
        coordinator.ObserveDecodedSamples(source, ActiveSamples());
        coordinator.ObserveTraffic(source, Terminator(100, 77));

        Assert.Equal(2, targetSystem.Sent.Count);
        Assert.All(targetSystem.Sent, sent => Assert.Equal(FneTrafficProtocol.Analog, sent.Protocol));
        Assert.Equal((byte)AnalogAudioFrameType.VoiceStart, targetSystem.Sent[0].Payload[AnalogVoicePacketCodec.FrameTypeOffset]);
        Assert.Equal((byte)AnalogAudioFrameType.Terminator, targetSystem.Sent[1].Payload[AnalogVoicePacketCodec.FrameTypeOffset]);
        Assert.False(coordinator.GroupNames.Count == 0);
    }

    [Fact]
    public void DecodedSamplesUseSuppliedStreamIdentityAfterChannelChanges()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = Create("Source", 100, 1001);
        (ChannelViewModel target, FakeEndpoint targetSystem) = Create("Target", 200, 2002);
        using var coordinator = new PatchForwardingCoordinator([sourceSystem, targetSystem]);
        coordinator.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });

        ObserveVoice(coordinator, source, 77, 7001);
        Assert.True(source.TryApplyTraffic(
            "Source",
            new FneTrafficFrame(
                FneTrafficProtocol.Analog,
                1,
                8001,
                100,
                null,
                "GROUP",
                "VOICE",
                "VOICE",
                2,
                78,
                new byte[AnalogVoicePacketCodec.PacketBytes])));

        coordinator.ObserveDecodedSamples(source, streamId: 77, sourceId: 7001, ActiveSamples());

        Assert.Single(targetSystem.Sent);
        Assert.Equal((byte)AnalogAudioFrameType.VoiceStart, targetSystem.Sent[0].Payload[AnalogVoicePacketCodec.FrameTypeOffset]);
    }

    [Fact]
    public void UsesFallbackOrPassthroughSourceIdAndHonorsOneWayRoutes()
    {
        (ChannelViewModel first, FakeEndpoint firstSystem) = Create("First", 100, 1001);
        (ChannelViewModel second, FakeEndpoint secondSystem) = Create("Second", 200, 2002);
        using var coordinator = new PatchForwardingCoordinator([firstSystem, secondSystem]);
        coordinator.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>> { ["Patch"] = [new("First", 100), new("Second", 200)] },
            new Dictionary<string, bool> { ["Patch"] = true });

        ObserveVoice(coordinator, second, 8, 7777);
        coordinator.ObserveDecodedSamples(second, ActiveSamples());
        Assert.Empty(firstSystem.Sent); // one-way permits only the first member as source

        ObserveVoice(coordinator, first, 9, 7777);
        coordinator.ObserveDecodedSamples(first, ActiveSamples());
        Assert.All(secondSystem.Sent, sent => Assert.Equal(2002u, ReadUInt24(sent.Payload, AnalogVoicePacketCodec.SourceIdOffset)));

        coordinator.StopAll();
        secondSystem.Sent.Clear();
        coordinator.SourceIdPassthrough = true;
        coordinator.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("First", 100), new("Second", 200)]
        });
        ObserveVoice(coordinator, first, 10, 7777);
        coordinator.ObserveDecodedSamples(first, ActiveSamples());
        Assert.All(secondSystem.Sent, sent => Assert.Equal(7777u, ReadUInt24(sent.Payload, AnalogVoicePacketCodec.SourceIdOffset)));
    }

    [Fact]
    public void SkipsDisconnectedOrUntransmittableTargets()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = Create("Source", 100, 1001);
        (ChannelViewModel target, FakeEndpoint targetSystem) = Create("Target", 200, 2002, connected: false);
        using var coordinator = new PatchForwardingCoordinator([sourceSystem, targetSystem]);
        coordinator.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });

        ObserveVoice(coordinator, source, 1, 3000);
        coordinator.ObserveDecodedSamples(source, ActiveSamples());

        Assert.Empty(targetSystem.Sent);
    }

    [Fact]
    public void ForwardsDecodedAudioToNxdnTargetLifecycle()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = Create("Source", 100, 1001);
        var target = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "NXDN target",
            System = "Target",
            Tgid = "200",
            Mode = "nxdn"
        });
        var targetSystem = new FakeEndpoint("Target", [target], 2002, true);
        using var coordinator = new PatchForwardingCoordinator(
            [sourceSystem, targetSystem],
            createVocoderBackend: () => new FakeVocoderBackend());
        coordinator.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });

        ObserveVoice(coordinator, source, 77, 7001);
        for (int index = 0; index < NxdnVoicePacketCodec.CodewordsPerFrame; index++)
            coordinator.ObserveDecodedSamples(source, ActiveSamples());
        coordinator.ObserveTraffic(source, Terminator(100, 77));

        Assert.Equal(3, targetSystem.Sent.Count);
        Assert.All(targetSystem.Sent, sent => Assert.Equal(FneTrafficProtocol.Nxdn, sent.Protocol));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, targetSystem.Sent[0].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.TransmitReleaseMessageType, targetSystem.Sent[2].Payload[4]);
    }

    private static (ChannelViewModel Channel, FakeEndpoint System) Create(string system, uint talkgroup, uint sourceId, bool connected = true)
    {
        var channel = new ChannelViewModel(new ChannelConfiguration { Name = system, System = system, Tgid = talkgroup.ToString(), Mode = "analog" });
        return (channel, new FakeEndpoint(system, [channel], sourceId, connected));
    }

    private static void ObserveVoice(PatchForwardingCoordinator coordinator, ChannelViewModel channel, uint streamId, uint sourceId)
    {
        FneTrafficFrame voice = new(FneTrafficProtocol.Analog, 1, sourceId, channel.Definition.DestinationId, null, "GROUP", "VOICE", "VOICE", 1, streamId, new byte[AnalogVoicePacketCodec.PacketBytes]);
        Assert.True(channel.TryApplyTraffic(channel.Definition.SystemName, voice));
        coordinator.ObserveTraffic(channel, voice);
    }

    private static FneTrafficFrame Terminator(uint destinationId, uint streamId)
        => new(FneTrafficProtocol.Analog, 1, 7001, destinationId, null, "GROUP", "TERMINATOR", "TERMINATOR", 2, streamId, new byte[AnalogVoicePacketCodec.PacketBytes]);

    private static uint ReadUInt24(byte[] data, int offset) => (uint)(data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2]);

    private static short[] ActiveSamples()
    {
        var samples = new short[160];
        samples[0] = 12_000;
        return samples;
    }

    private sealed class FakeEndpoint(string name, IReadOnlyList<ChannelViewModel> channels, uint sourceId, bool connected) : IFneTrafficEndpoint
    {
        private uint streamId;
        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public bool IsConnected => connected;
        public uint? SourceId => sourceId;
        public List<(FneTrafficProtocol Protocol, byte[] Payload)> Sent { get; } = [];
        public uint CreateStreamId() => ++streamId;
        public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort sequence, uint outboundStreamId)
            => Sent.Add((protocol, payload.ToArray()));
    }

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public string Name => "fake";
        public bool IsAvailable => true;
        public IVocoderSession CreateSession(VocoderMode mode) => new FakeVocoderSession();
        public void Dispose() { }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() { }
    }
}
