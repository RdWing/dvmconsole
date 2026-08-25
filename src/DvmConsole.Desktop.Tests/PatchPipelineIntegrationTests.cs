using System.Collections.Concurrent;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchPipelineIntegrationTests
{
    [Fact]
    public async Task MixedModePatchTranscodesNativeAudioInBothDirections()
    {
        (ChannelViewModel p25, FakeEndpoint p25System) = CreateChannel(
            "P25 FNE",
            "P25 Dispatch",
            destinationId: 747,
            sourceId: 3_222_223,
            mode: "p25");
        (ChannelViewModel dmr, FakeEndpoint dmrSystem) = CreateChannel(
            "DMR FNE",
            "DMR Dispatch",
            destinationId: 99,
            sourceId: 890,
            mode: "dmr",
            slot: 1);
        using var forwarding = new PatchForwardingCoordinator(
            [p25System, dmrSystem],
            createVocoderBackend: () => new SoftwareVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new SoftwareVocoderBackend());
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Mixed Mode"] =
            [
                new(p25.Definition.SystemName, p25.Definition.DestinationId),
                new(dmr.Definition.SystemName, dmr.Definition.DestinationId)
            ]
        });
        await decoding.ApplyChannelsAsync([p25, dmr]);

        short[] sourceAudio = CreateTestAudio(P25DfsiFrameCodec.CodewordsPerLdu);
        FneTrafficFrame p25Voice = CreateNativeP25Voice(p25, sourceAudio, sourceId: 7_471, streamId: 101);
        forwarding.ObserveTraffic(p25, p25Voice);
        Assert.Equal(0, await decoding.ProcessAsync(p25, p25Voice));

        await WaitForSentCountAsync(dmrSystem, 4);
        SentPacket[] dmrVoicePackets = dmrSystem.Sent.Skip(1).ToArray();
        Assert.Equal(3, dmrVoicePackets.Length);
        Assert.All(dmrVoicePackets, packet => Assert.Equal(FneTrafficProtocol.Dmr, packet.Protocol));
        AssertNativeDmrAudio(dmrVoicePackets);

        forwarding.StopSource(p25, 101);
        dmrSystem.ClearSent();
        p25System.ClearSent();

        IReadOnlyList<FneTrafficFrame> dmrVoice = CreateNativeDmrVoice(
            dmr,
            sourceAudio,
            sourceId: 8_901,
            streamId: 202);
        foreach (FneTrafficFrame voice in dmrVoice)
        {
            forwarding.ObserveTraffic(dmr, voice);
            Assert.Equal(0, await decoding.ProcessAsync(dmr, voice));
        }

        await WaitForSentCountAsync(p25System, 2);
        SentPacket p25Ldu = Assert.Single(p25System.Sent, packet =>
            packet.Protocol == FneTrafficProtocol.P25 &&
            packet.Payload[22] is P25DfsiFrameCodec.Ldu1Duid or P25DfsiFrameCodec.Ldu2Duid);
        AssertNativeP25Audio(p25Ldu);
    }

    [Fact]
    public async Task ExactMemberIdentitySelectsDmrWhenSameFneAndTalkgroupAlsoHaveP25()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = CreateChannel(
            "Source FNE",
            "P25 Source",
            destinationId: 747,
            sourceId: 3_222_223,
            mode: "p25");
        var p25Collision = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "P25 99",
            System = "Destination FNE",
            Tgid = "99",
            Mode = "p25"
        });
        var dmrTarget = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "DMR 99",
            System = "Destination FNE",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        var destinationSystem = new FakeEndpoint(
            "Destination FNE",
            [p25Collision, dmrTarget],
            sourceId: 890);
        using var forwarding = new PatchForwardingCoordinator(
            [sourceSystem, destinationSystem],
            createVocoderBackend: () => new SoftwareVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new SoftwareVocoderBackend());
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Cross Mode"] =
            [
                new(source.Definition.SystemName, source.Definition.DestinationId, source.Name),
                new(dmrTarget.Definition.SystemName, dmrTarget.Definition.DestinationId, dmrTarget.Name)
            ]
        });
        await decoding.ApplyChannelsAsync([source, dmrTarget]);

        short[] sourceAudio = CreateTestAudio(P25DfsiFrameCodec.CodewordsPerLdu);
        FneTrafficFrame voice = CreateNativeP25Voice(source, sourceAudio, sourceId: 7_471, streamId: 303);
        forwarding.ObserveTraffic(source, voice);
        Assert.Equal(0, await decoding.ProcessAsync(source, voice));

        await WaitForSentCountAsync(destinationSystem, 4);
        Assert.All(destinationSystem.Sent, packet => Assert.Equal(FneTrafficProtocol.Dmr, packet.Protocol));
        AssertNativeDmrAudio(destinationSystem.Sent.Skip(1));
    }

    [Fact]
    public async Task TwoMemberP25PatchForwardsCompleteCallsInBothDirections()
    {
        (ChannelViewModel first, FakeEndpoint firstSystem) = CreateChannel(
            "TYF",
            "747 Select P25",
            destinationId: 747,
            sourceId: 3_222_223);
        (ChannelViewModel second, FakeEndpoint secondSystem) = CreateChannel(
            "TEST FNE",
            "PARROT P25",
            destinationId: 9_990,
            sourceId: 890);
        using var forwarding = new PatchForwardingCoordinator(
            [firstSystem, secondSystem],
            createVocoderBackend: () => new FakeVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new FakeVocoderBackend());
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch Test 1"] =
            [
                new(first.Definition.SystemName, first.Definition.DestinationId),
                new(second.Definition.SystemName, second.Definition.DestinationId)
            ]
        });
        await decoding.ApplyChannelsAsync([first, second]);

        await ForwardCallAsync(
            first,
            firstSystem,
            decoding,
            forwarding,
            sourceId: 7_471,
            streamId: 101);

        await WaitForSentCountAsync(secondSystem, 3);
        AssertP25Call(secondSystem, expectedDestinationId: 9_990);

        await ForwardCallAsync(
            second,
            secondSystem,
            decoding,
            forwarding,
            sourceId: 9_901,
            streamId: 202);

        await WaitForSentCountAsync(firstSystem, 3);
        AssertP25Call(firstSystem, expectedDestinationId: 747);
    }

    private static async Task ForwardCallAsync(
        ChannelViewModel source,
        FakeEndpoint sourceSystem,
        PatchSourceDecodeCoordinator decoding,
        PatchForwardingCoordinator forwarding,
        uint sourceId,
        uint streamId)
    {
        IReadOnlyDictionary<(FneTrafficProtocol, uint), ChannelViewModel[]> routes =
            new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
            {
                [(FneTrafficProtocol.P25, source.Definition.DestinationId)] = [source]
            };
        FneTrafficFrame voice = CreateVoice(source, sourceId, streamId);
        ReceiveIngressRoutingDecision ingress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            voice,
            decoding.IsTrackingStream);
        ChannelViewModel target = Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            decoding.ActiveChannels,
            voice,
            ingress,
            decoding.IsTrackingStream));

        forwarding.ObserveTraffic(target, voice);
        Assert.Equal(0, await decoding.ProcessAsync(target, voice));

        FneTrafficFrame terminator = CreateTerminator(source, sourceId, streamId);
        ReceiveIngressRoutingDecision terminatorIngress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            terminator,
            decoding.IsTrackingStream);
        ChannelViewModel terminatorTarget = Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            decoding.ActiveChannels,
            terminator,
            terminatorIngress,
            decoding.IsTrackingStream));
        Assert.Same(target, terminatorTarget);
        forwarding.StopSource(terminatorTarget, streamId);
        await decoding.ProcessAsync(terminatorTarget, terminator);

        Assert.DoesNotContain(sourceSystem.Sent, packet => packet.StreamId == streamId);
    }

    private static FneTrafficFrame CreateVoice(
        ChannelViewModel channel,
        uint sourceId,
        uint streamId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 0,
            streamId,
            P25DfsiFrameCodec.CreateLdu1Payload(
                sourceId,
                channel.Definition.DestinationId,
                new byte[P25DfsiFrameCodec.ImbeBytes]));

    private static FneTrafficFrame CreateTerminator(
        ChannelViewModel channel,
        uint sourceId,
        uint streamId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "TERMINATOR",
            subtype: "TDU",
            packetSequence: P25DfsiFrameCodec.RtpCallEndSequence,
            streamId,
            P25DfsiFrameCodec.CreateTduPayload(
                sourceId,
                channel.Definition.DestinationId,
                grantDemand: false));

    private static void AssertP25Call(FakeEndpoint endpoint, uint expectedDestinationId)
    {
        Assert.True(endpoint.Sent.Count >= 3);
        Assert.All(endpoint.Sent, packet => Assert.Equal(FneTrafficProtocol.P25, packet.Protocol));
        Assert.Equal(P25DfsiFrameCodec.TduDuid, endpoint.Sent[0].Payload[22]);
        Assert.Contains(endpoint.Sent, packet =>
            packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid &&
            packet.StreamId != 0);
        SentPacket ldu = endpoint.Sent.First(packet =>
            packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid);
        Assert.True(P25DfsiFrameCodec.TryExtractCallIdentifiers(
            new FneTrafficFrame(
                FneTrafficProtocol.P25,
                peerId: 1,
                sourceId: 0,
                destinationId: 0,
                slot: null,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: "LDU1",
                packetSequence: ldu.PacketSequence,
                streamId: ldu.StreamId,
                ldu.Payload),
            out _,
            out uint destinationId));
        Assert.Equal(expectedDestinationId, destinationId);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, endpoint.Sent[^1].Payload[22]);
    }

    private static (ChannelViewModel Channel, FakeEndpoint System) CreateChannel(
        string systemName,
        string channelName,
        uint destinationId,
        uint sourceId,
        string mode = "p25",
        byte slot = 1)
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = channelName,
            System = systemName,
            Tgid = destinationId.ToString(),
            Mode = mode,
            Slot = slot
        });
        return (channel, new FakeEndpoint(systemName, [channel], sourceId));
    }

    private static short[] CreateTestAudio(int frameCount)
    {
        var samples = new short[frameCount * VocoderFrameSizes.PcmSamplesPerFrame];
        for (int index = 0; index < samples.Length; index++)
        {
            double time = index / 8_000d;
            double envelope = 0.65 + 0.35 * Math.Sin(2 * Math.PI * 3 * time);
            samples[index] = (short)(envelope * (
                10_000 * Math.Sin(2 * Math.PI * 220 * time) +
                4_000 * Math.Sin(2 * Math.PI * 660 * time)));
        }
        return samples;
    }

    private static FneTrafficFrame CreateNativeP25Voice(
        ChannelViewModel channel,
        ReadOnlySpan<short> samples,
        uint sourceId,
        uint streamId)
    {
        var packets = new List<SentPacket>();
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession vocoder = backend.CreateSession(VocoderMode.P25Imbe);
        using var encoder = new P25TxAudioSession(
            sourceId,
            channel.Definition.DestinationId,
            streamId,
            vocoder,
            (payload, sequence, outboundStreamId) => packets.Add(new SentPacket(
                FneTrafficProtocol.P25,
                payload.ToArray(),
                sequence,
                outboundStreamId)));

        Assert.Equal(1, encoder.Process(samples));
        SentPacket packet = Assert.Single(packets);
        return new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packet.PacketSequence,
            streamId,
            packet.Payload);
    }

    private static IReadOnlyList<FneTrafficFrame> CreateNativeDmrVoice(
        ChannelViewModel channel,
        ReadOnlySpan<short> samples,
        uint sourceId,
        uint streamId)
    {
        var packets = new List<SentPacket>();
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession vocoder = backend.CreateSession(VocoderMode.DmrAmbe);
        using var encoder = new DmrTxAudioSession(
            sourceId,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            streamId,
            vocoder,
            (payload, sequence, outboundStreamId) => packets.Add(new SentPacket(
                FneTrafficProtocol.Dmr,
                payload.ToArray(),
                sequence,
                outboundStreamId)));

        Assert.Equal(3, encoder.Process(samples));
        return packets.Select((packet, index) => new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            callType: "GROUP",
            frameType: index == 0 ? "VOICE_SYNC" : "VOICE",
            subtype: "VOICE",
            packet.PacketSequence,
            streamId,
            packet.Payload)).ToArray();
    }

    private static void AssertNativeDmrAudio(IEnumerable<SentPacket> packets)
    {
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession decoder = backend.CreateSession(VocoderMode.DmrAmbe);
        var decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        long absoluteSampleTotal = 0;
        int sampleCount = 0;
        foreach (SentPacket packet in packets)
        {
            byte[] ambe = DmrVoicePacketCodec.ExtractAmbe(packet.Payload);
            for (int offset = 0; offset < ambe.Length; offset += VocoderFrameSizes.HalfRateCodewordBytes)
            {
                Assert.Equal(0, decoder.Decode(
                    ambe.AsSpan(offset, VocoderFrameSizes.HalfRateCodewordBytes),
                    decoded));
                absoluteSampleTotal += decoded.Sum(sample => Math.Abs((int)sample));
                sampleCount += decoded.Length;
            }
        }

        Assert.True(absoluteSampleTotal / sampleCount > 100);
    }

    private static void AssertNativeP25Audio(SentPacket packet)
    {
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 890,
            destinationId: 747,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid ? "LDU1" : "LDU2",
            packet.PacketSequence,
            packet.StreamId,
            packet.Payload);
        byte[] imbe = new byte[P25DfsiFrameCodec.ImbeBytes];
        bool[] available = new bool[P25DfsiFrameCodec.CodewordsPerLdu];
        Assert.True(P25DfsiFrameCodec.TryExtractImbeFrames(traffic, imbe, available));
        Assert.All(available, Assert.True);

        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession decoder = backend.CreateSession(VocoderMode.P25Imbe);
        var decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        long absoluteSampleTotal = 0;
        for (int offset = 0; offset < imbe.Length; offset += P25DfsiFrameCodec.CodewordBytes)
        {
            Assert.Equal(0, decoder.Decode(
                imbe.AsSpan(offset, P25DfsiFrameCodec.CodewordBytes),
                decoded));
            absoluteSampleTotal += decoded.Sum(sample => Math.Abs((int)sample));
        }

        Assert.True(absoluteSampleTotal / (decoded.Length * available.Length) > 100);
    }

    private static async Task WaitForSentCountAsync(FakeEndpoint endpoint, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (endpoint.Sent.Count < expectedCount)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeEndpoint(
        string name,
        IReadOnlyList<ChannelViewModel> channels,
        uint sourceId) : IFneTrafficEndpoint
    {
        private uint nextStreamId;
        private readonly ConcurrentQueue<SentPacket> sent = new();

        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public bool IsConnected => true;
        public uint? SourceId => sourceId;
        public IReadOnlyList<SentPacket> Sent => sent.ToArray();

        public uint CreateStreamId() => ++nextStreamId;

        public void SendTraffic(
            FneTrafficProtocol protocol,
            ReadOnlySpan<byte> payload,
            ushort sequence,
            uint streamId)
            => sent.Enqueue(new SentPacket(protocol, payload.ToArray(), sequence, streamId));

        public void ClearSent() => sent.Clear();
    }

    private sealed record SentPacket(
        FneTrafficProtocol Protocol,
        byte[] Payload,
        ushort PacketSequence,
        uint StreamId);

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public string Name => "Patch integration fake";
        public bool IsAvailable => true;
        public IVocoderSession CreateSession(VocoderMode mode) => new FakeVocoderSession();
        public void Dispose() { }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Fill(0x5A);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            samples.Fill(12_000);
            return 0;
        }

        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() { }
    }
}
