using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.DMR;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrVoicePacketCodecTests
{
    [Fact]
    public void ExtractsThreeAmbeCodewordsFromDmrPacketLayout()
    {
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        for (int index = 0; index < DmrVoicePacketCodec.FrameBytes; index++)
            packet[DmrVoicePacketCodec.HeaderBytes + index] = (byte)index;

        byte[] ambe = DmrVoicePacketCodec.ExtractAmbe(packet);

        Assert.Equal(27, ambe.Length);
        Assert.Equal(Enumerable.Range(0, 13).Select(value => (byte)value), ambe[..13]);
        Assert.Equal((byte)3, ambe[13]);
        Assert.Equal(Enumerable.Range(20, 13).Select(value => (byte)value), ambe[14..]);
    }

    [Fact]
    public void CreatesDmrVoicePacketThatRoundTripsAmbeLayout()
    {
        byte[] ambe = Enumerable.Range(0, DmrVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)value)
            .ToArray();

        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 1,
            voiceSync: false,
            embeddedSequence: 3,
            frameSequence: 7,
            ambe);

        Assert.Equal(DmrVoicePacketCodec.PacketBytes, packet.Length);
        Assert.Equal("DMRD", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal((byte)7, packet[4]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, packet[5..8]);
        Assert.Equal(new byte[] { 0xA0, 0xB0, 0xC0 }, packet[8..11]);
        Assert.Equal((byte)0x83, packet[15]);
        Assert.Equal(ambe, DmrVoicePacketCodec.ExtractAmbe(packet));
    }

    [Fact]
    public void ExtractsProtocolEncryptionFromPrivacyIndicatorHeader()
    {
        byte[] frame = new byte[DmrVoicePacketCodec.FrameBytes];
        var privacy = new PrivacyLC
        {
            AlgId = DmrPrivacyAlgorithms.Arc4,
            FID = DmrPrivacyAlgorithms.FeatureId,
            KId = 0x55,
            Group = true,
            DstId = 100
        };
        FullLC.EncodePI(privacy, ref frame);
        new SlotType { ColorCode = 0, DataType = (byte)DMRDataType.VOICE_PI_HEADER }.GetData(ref frame);
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        frame.CopyTo(packet, DmrVoicePacketCodec.HeaderBytes);

        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(
            packet,
            out DmrVoicePacketCodec.DmrEncryptionMetadata metadata));
        Assert.Equal(DmrPrivacyAlgorithms.Arc4, metadata.AlgorithmId);
        Assert.Equal((byte)0x55, metadata.KeyId);
        Assert.Equal(new byte[4], metadata.MessageIndicator);
    }

    [Fact]
    public void RejectsPrivacyIndicatorFromAnotherFeatureSet()
    {
        byte[] frame = new byte[DmrVoicePacketCodec.FrameBytes];
        var privacy = new PrivacyLC
        {
            AlgId = DmrPrivacyAlgorithms.Arc4,
            FID = 0x20,
            KId = 0x55,
            Group = true,
            DstId = 100
        };
        FullLC.EncodePI(privacy, ref frame);
        new SlotType { ColorCode = 0, DataType = (byte)DMRDataType.VOICE_PI_HEADER }
            .GetData(ref frame);
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        frame.CopyTo(packet, DmrVoicePacketCodec.HeaderBytes);

        Assert.False(DmrVoicePacketCodec.TryExtractEncryptionMetadata(packet, out _));
    }

    [Fact]
    public async Task VoiceBurstTwoIsNotMisclassifiedAsPrivacyIndicator()
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 100,
            slot: 1,
            voiceSync: false,
            embeddedSequence: 2,
            frameSequence: 1,
            new byte[DmrVoicePacketCodec.AmbeBytes]);
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);

        Assert.False(DmrVoicePacketCodec.IsPrivacyIndicator(packet));

        Assert.Equal(0, await session.ProcessAsync(CreateTraffic(
            100,
            1,
            "VOICE",
            payload: packet)));
        Assert.Equal(DmrVoicePacketCodec.CodewordsPerPacket, session.FramesDecoded);
        Assert.Equal(DmrVoicePacketCodec.CodewordsPerPacket, vocoder.DecodeCalls);
    }

    [Theory]
    [InlineData((byte)0, (byte)0x10)]
    [InlineData((byte)1, (byte)0x90)]
    public void EncodesZeroBasedSlotInDmrNetworkHeader(byte slot, byte expectedHeader)
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 2,
            slot,
            voiceSync: true,
            embeddedSequence: 0,
            frameSequence: 0,
            new byte[DmrVoicePacketCodec.AmbeBytes]);

        Assert.Equal(expectedHeader, packet[15]);
    }

    [Theory]
    [InlineData((byte)0, (byte)0x21)]
    [InlineData((byte)1, (byte)0xA1)]
    public void VoiceLinkControlHeaderUsesDataSyncEnvelope(byte slot, byte expectedHeader)
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId: 1,
            destinationId: 2,
            slot,
            frameSequence: 0);

        Assert.Equal(expectedHeader, packet[15]);
    }

    [Fact]
    public void VoiceLinkControlHeaderIsNotClassifiedAsPrivacyIndicator()
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            frameSequence: 0,
            encrypted: true);

        Assert.False(DmrVoicePacketCodec.IsPrivacyIndicator(packet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VoiceLinkControlHeaderReportsEncryptionState(bool encrypted)
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            frameSequence: 0,
            encrypted);

        Assert.True(DmrVoicePacketCodec.TryExtractVoiceEncryptionState(packet, out bool decoded));
        Assert.Equal(encrypted, decoded);
        LC linkControl = Assert.IsType<LC>(FullLC.Decode(
            packet[DmrVoicePacketCodec.HeaderBytes..],
            DMRDataType.VOICE_LC_HEADER));
        Assert.Equal(
            encrypted ? DmrPrivacyAlgorithms.FeatureId : (byte)0,
            linkControl.FID);
        Assert.Equal(encrypted ? 0x40 : 0, linkControl.GetBytes()[2] & 0x40);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminatorPreservesEncryptionServiceOption(bool encrypted)
    {
        byte[] packet = DmrVoicePacketCodec.CreateTerminatorPacket(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            frameSequence: 7,
            encrypted);

        LC linkControl = Assert.IsType<LC>(FullLC.Decode(
            packet[DmrVoicePacketCodec.HeaderBytes..],
            DMRDataType.TERMINATOR_WITH_LC));
        Assert.Equal(
            encrypted ? DmrPrivacyAlgorithms.FeatureId : (byte)0,
            linkControl.FID);
        Assert.Equal(encrypted, linkControl.Encrypted);
        Assert.Equal(encrypted ? 0x40 : 0, linkControl.GetBytes()[2] & 0x40);
    }

    [Fact]
    public async Task DmrSessionDecodesThreeCodewordsToPlaybackFrames()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            3,
            0,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]);

        int errors = await session.ProcessAsync(traffic);

        Assert.Equal(0, errors);
        Assert.Equal(3, session.FramesDecoded);
        Assert.Equal(3, vocoder.DecodeCalls);
        Assert.Equal(3, playback.Frames.Count);
        Assert.All(playback.Frames, frame => Assert.Equal(160, frame.Length));
    }

    [Fact]
    public async Task DmrSessionIgnoresNonVoiceFrames()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            3,
            0,
            "GROUP",
            "TERMINATOR",
            "TERMINATOR",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]);

        int errors = await session.ProcessAsync(traffic);

        Assert.Equal(0, errors);
        Assert.Equal(0, session.FramesDecoded);
        Assert.Equal(0, vocoder.DecodeCalls);
        Assert.Empty(playback.Frames);
    }

    [Fact]
    public async Task DmrSessionIgnoresMalformedVoiceAndRecoversOnNextPacket()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);

        FneTrafficFrame malformed = new(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[10]);
        Assert.Equal(0, await session.ProcessAsync(malformed));
        Assert.Equal(0, session.FramesDecoded);
        Assert.Equal(1, session.MalformedPackets);

        Assert.Equal(0, await session.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 2)));
        Assert.Equal(3, session.FramesDecoded);
        Assert.Equal(3, playback.Frames.Count);
    }

    [Fact]
    public void SelectorMatchesOnlyTheConfiguredDmrVoiceStream()
    {
        var selector = new DmrTrafficSelector(destinationId: 100, slot: 1);

        Assert.True(selector.Matches(CreateTraffic(100, 1, "VOICE")));
        Assert.True(selector.Matches(CreateTraffic(100, 1, "VOICE_SYNC")));
        Assert.False(selector.Matches(CreateTraffic(101, 1, "VOICE")));
        Assert.False(selector.Matches(CreateTraffic(100, 0, "VOICE")));
        Assert.False(selector.Matches(CreateTraffic(100, 1, "TERMINATOR")));
    }

    [Fact]
    public async Task RouterDeliversPrivacyIndicatorWithoutInventingPacketLoss()
    {
        byte[] key = Convert.FromHexString("0102030405");
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Arc4,
            keyId: 7,
            key,
            Convert.FromHexString("12345678"));
        using var keys = new DmrKeyRing("System A", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    Protocol = "dmr",
                    AlgId = DmrPrivacyAlgorithms.Arc4,
                    KeyId = 7,
                    Key = Convert.ToHexString(key)
                }
            ]
        });
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(
            new DmrTrafficSelector(100, 1),
            vocoder,
            playback,
            keys,
            "System A");
        byte[] privacy = DmrVoicePacketCodec.CreatePrivacyIndicatorPacket(
            1, 100, 1, 1, options);
        byte[] voice = DmrVoicePacketCodec.CreateVoicePacket(
            1, 100, 1, true, 0, 2, new byte[DmrVoicePacketCodec.AmbeBytes]);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
            100, 1, "DATA_SYNC", packetSequence: 10, payload: privacy)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
            100, 1, "VOICE_SYNC", packetSequence: 11, payload: voice)));

        Assert.Equal(0, router.LostPackets);
        Assert.Equal(0, router.MalformedPackets);
        Assert.Equal(3, router.FramesDecoded);
        Assert.Equal(3, vocoder.ParameterDecodeCalls);
        Assert.Equal(0, vocoder.DecodeCalls);
    }

    [Fact]
    public async Task RouterDoesNotLetVoiceHeaderConsumeFirstVoiceSequence()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(
            new DmrTrafficSelector(100, 1),
            vocoder,
            playback);
        byte[] header = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId: 1,
            destinationId: 100,
            slot: 1,
            frameSequence: 0,
            encrypted: false);
        byte[] voice = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 100,
            slot: 1,
            voiceSync: true,
            embeddedSequence: 0,
            frameSequence: 0,
            ambe: new byte[DmrVoicePacketCodec.AmbeBytes]);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
            100,
            1,
            "DATA_SYNC",
            packetSequence: 10,
            payload: header)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
            100,
            1,
            "VOICE_SYNC",
            packetSequence: 10,
            payload: voice)));

        Assert.Equal(0, router.LostPackets);
        Assert.Equal(0, router.DuplicateOrLatePackets);
        Assert.Equal(3, router.FramesDecoded);
        Assert.Equal(3, vocoder.DecodeCalls);
        Assert.Equal(3, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableReceiverUsesLateEntryMetadataWhenStartupHeadersWereMissed()
    {
        byte[] key = Convert.FromHexString("0102030405");
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Arc4,
            keyId: 7,
            key,
            Convert.FromHexString("12345678"));
        var packets = new List<byte[]>();
        using (var transmitter = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 100,
            slot: 1,
            streamId: 99,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: options))
        {
            transmitter.Start();
            transmitter.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 36]);
        }
        byte[][] voicePackets = packets.Skip(2).ToArray();
        Assert.Equal(12, voicePackets.Length);

        using var keys = new DmrKeyRing("System A", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    Protocol = "dmr",
                    AlgId = DmrPrivacyAlgorithms.Arc4,
                    KeyId = 7,
                    Key = Convert.ToHexString(key)
                }
            ]
        });
        var receiverVocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var receiver = new DmrRxAudioSession(
            receiverVocoder,
            playback,
            keys,
            "System A",
            privacyMayVary: true);

        for (int index = 0; index < voicePackets.Length; index++)
        {
            string frameType = index % 6 == 0 ? "VOICE_SYNC" : "VOICE";
            await receiver.ProcessAsync(CreateTraffic(
                100,
                1,
                frameType,
                packetSequence: (ushort)index,
                payload: voicePackets[index]));
        }

        Assert.Equal(18, receiver.FramesDecoded);
        Assert.Equal(18, receiverVocoder.ParameterDecodeCalls);
        Assert.Equal(0, receiverVocoder.DecodeCalls);
        Assert.Equal(18, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableReceiverResolvesClearLateEntryWithReverseChannelSignaling()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var receiver = new DmrRxAudioSession(
            vocoder,
            playback,
            privacyMayVary: true);

        for (int index = 0; index < 12; index++)
        {
            byte voiceBurst = (byte)(index % 6);
            DmrBurstFSignaling? signaling = voiceBurst == 5
                ? new DmrBurstFSignaling(IsReverseChannel: true, Payload: 0x0A5)
                : null;
            byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
                sourceId: 1,
                destinationId: 100,
                slot: 1,
                voiceSync: voiceBurst == 0,
                embeddedSequence: voiceBurst,
                frameSequence: (byte)index,
                ambe: new byte[DmrVoicePacketCodec.AmbeBytes],
                burstFSignaling: signaling);
            string frameType = voiceBurst == 0 ? "VOICE_SYNC" : "VOICE";

            await receiver.ProcessAsync(CreateTraffic(
                100,
                1,
                frameType,
                packetSequence: (ushort)index,
                payload: packet));
        }

        Assert.Equal(21, receiver.FramesDecoded);
        Assert.Equal(21, vocoder.DecodeCalls);
        Assert.Equal(0, vocoder.ParameterDecodeCalls);
        Assert.Equal(21, playback.Frames.Count);
    }

    [Fact]
    public async Task RouterDecodesOnlySelectedTraffic()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(101, 1, "VOICE")));
        Assert.Equal(0, router.FramesDecoded);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE")));
        Assert.Equal(3, router.FramesDecoded);
        Assert.Equal(3, playback.Frames.Count);
    }

    [Fact]
    public async Task RouterDropsDuplicateLateAndCountsSkippedPackets()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 10)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 12)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 11)));

        Assert.Equal(1, router.LostPackets);
        Assert.Equal(1, router.DuplicateOrLatePackets);
        Assert.Equal(9, router.FramesDecoded);
    }

    [Fact]
    public async Task RouterConcealsMalformedVoiceWithoutSkippingCodecTime()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 1)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
            100, 1, "VOICE", packetSequence: 2, payload: new byte[10])));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 3)));

        Assert.Equal(0, router.LostPackets);
        Assert.Equal(1, router.MalformedPackets);
        Assert.Equal(3, vocoder.DecodeLostCalls);
        Assert.Equal(9, router.FramesDecoded);
    }

    [Fact]
    public async Task RouterResetsDecoderAfterBoundedLongLossConcealment()
    {
        var vocoder = new FakeVocoderSession();
        await using var router = new DmrRxAudioRouter(
            new DmrTrafficSelector(100, 1),
            vocoder,
            new FakePlayback());

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 1)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 13)));

        Assert.Equal(11, router.LostPackets);
        Assert.Equal(30, vocoder.DecodeLostCalls);
        Assert.Equal(1, vocoder.ResetCalls);
        Assert.Equal(36, router.FramesDecoded);
    }

    [Fact]
    public async Task RouterResetsSequenceOnNewStreamAndHandlesWrap()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: ushort.MaxValue - 1, streamId: 99)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 0, streamId: 99)));
        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE", packetSequence: 0, streamId: 100)));

        Assert.Equal(0, router.LostPackets);
        Assert.Equal(9, router.FramesDecoded);
    }

    [Fact]
    public async Task RouterProcessesSustainedCallAcrossReservedSequenceBoundary()
    {
        const int packetCount = 1_024;
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        for (int index = 0; index < packetCount; index++)
        {
            // 0xFFFF is reserved for the DMR call-end marker. The media
            // sequence therefore wraps from 0xFFFE directly to zero.
            ushort packetSequence = index switch
            {
                0 => 0xFFFD,
                1 => 0xFFFE,
                _ => checked((ushort)(index - 2))
            };
            Assert.Equal(0, await router.ProcessAsync(CreateTraffic(
                100,
                1,
                "VOICE",
                packetSequence: packetSequence,
                streamId: 0x1234)));
        }

        Assert.Equal(0, router.LostPackets);
        Assert.Equal(0, router.DuplicateOrLatePackets);
        Assert.Equal(packetCount * DmrVoicePacketCodec.CodewordsPerPacket, router.FramesDecoded);
        Assert.Equal(router.FramesDecoded, playback.Frames.Count);
    }

    private static FneTrafficFrame CreateTraffic(
        uint destinationId,
        byte slot,
        string frameType,
        ushort packetSequence = 1,
        uint streamId = 99,
        byte[]? payload = null)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            destinationId,
            slot,
            "GROUP",
            frameType,
            frameType,
            packetSequence,
            streamId,
            payload ?? new byte[DmrVoicePacketCodec.PacketBytes]);
    }

    private sealed class FakeVocoderSession : IHalfRateVocoderSession
    {
        public int DecodeCalls { get; private set; }
        public int DecodeLostCalls { get; private set; }
        public int ParameterDecodeCalls { get; private set; }
        public int ResetCalls { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => codeword.Length;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill((short)DecodeCalls);
            return 0;
        }

        public int DecodeLost(Span<short> samples)
        {
            DecodeLostCalls++;
            samples.Clear();
            return 0;
        }

        public int FlushEncode(Span<byte> codeword) => 0;
        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters) => 0;
        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false)
        {
            ParameterDecodeCalls++;
            samples.Clear();
            return 0;
        }
        public int FlushEncodeParameters(Span<byte> parameters) => 0;
        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            codeword[..parameters.Length].CopyTo(parameters);
            return 0;
        }
        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
        {
            codeword.Clear();
            parameters.CopyTo(codeword);
        }
        public void Reset() => ResetCalls++;

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
