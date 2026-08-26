using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class DmrPrivacyTests
{
    public static TheoryData<byte, byte[]> Algorithms => new()
    {
        { DmrPrivacyAlgorithms.Arc4, Convert.FromHexString("0102030405") },
        { DmrPrivacyAlgorithms.DesOfb, Convert.FromHexString("133457799BBCDFF1") },
        { DmrPrivacyAlgorithms.Aes256, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray() }
    };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void PrivacyTransformRoundTripsAllEighteenCodewords(byte algorithmId, byte[] key)
    {
        var options = new DmrPrivacyOptions(
            algorithmId,
            keyId: 7,
            key,
            Convert.FromHexString("01234567"));
        using var encryptor = new DmrPrivacyProcessor(new FakeHalfRateSession(), options);
        using var decryptor = new DmrPrivacyProcessor(new FakeHalfRateSession(), options);

        for (int frame = 0; frame < 36; frame++)
        {
            byte[] clear = [1, 2, 3, 4, 5, (byte)frame, 0x80];
            byte[] encrypted = clear.ToArray();
            encryptor.ProcessParameters(encrypted);
            Assert.NotEqual(clear, encrypted);
            decryptor.ProcessParameters(encrypted);
            Assert.Equal(clear, encrypted);
        }
    }

    [Fact]
    public void Arc4MatchesDmraDiscardAndMessageIndicatorCycleVectors()
    {
        using var privacy = new DmrPrivacyProcessor(
            new FakeHalfRateSession(),
            new DmrPrivacyOptions(
                DmrPrivacyAlgorithms.Arc4,
                keyId: 1,
                Convert.FromHexString("0102030405"),
                Convert.FromHexString("12345678")));

        byte[] parameters = new byte[VocoderFrameSizes.HalfRateParameterBytes];
        privacy.ProcessParameters(parameters);
        Assert.Equal(Convert.FromHexString("1D3F3689D45680"), parameters);

        for (int codeword = 1; codeword < 18; codeword++)
            privacy.ProcessParameters(new byte[VocoderFrameSizes.HalfRateParameterBytes]);

        parameters.AsSpan().Clear();
        privacy.ProcessParameters(parameters);
        Assert.Equal(Convert.FromHexString("0858E632BF0D80"), parameters);
    }

    [Fact]
    public void PrivacyIndicatorRoundTripsAllMetadata()
    {
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Aes256,
            keyId: 0x55,
            new byte[32],
            Convert.FromHexString("12345678"));

        byte[] packet = DmrVoicePacketCodec.CreatePrivacyIndicatorPacket(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 1,
            frameSequence: 9,
            options);

        Assert.Equal((byte)0xA0, packet[15]);
        Assert.Equal(
            (byte)fnecore.DMR.DMRDataType.VOICE_PI_HEADER,
            new fnecore.DMR.SlotType(packet[DmrVoicePacketCodec.HeaderBytes..]).DataType);
        Assert.NotNull(fnecore.DMR.FullLC.DecodePI(packet[DmrVoicePacketCodec.HeaderBytes..]));
        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(packet, out var metadata));
        Assert.Equal(DmrPrivacyAlgorithms.Aes256, metadata.AlgorithmId);
        Assert.Equal((byte)0x55, metadata.KeyId);
        Assert.Equal(DmrPrivacyAlgorithms.FeatureId, metadata.FeatureId);
        Assert.Equal((uint)0xA0B0C0, metadata.DestinationId);
        Assert.True(metadata.Group);
        Assert.Equal(Convert.FromHexString("12345678"), metadata.MessageIndicator);
    }

    [Fact]
    public void PrivacyPreservesUnrecoverableFecStatusAcrossDecryption()
    {
        var vocoder = new FakeHalfRateSession
        {
            ExtractResult = HalfRateFecStatus.NativeUnrecoverableMarker
        };
        using var privacy = new DmrPrivacyProcessor(
            vocoder,
            new DmrPrivacyOptions(
                DmrPrivacyAlgorithms.Arc4,
                1,
                Convert.FromHexString("0102030405"),
                Convert.FromHexString("12345678")));
        Span<byte> parameters = stackalloc byte[VocoderFrameSizes.HalfRateParameterBytes];

        HalfRateFecStatus status = privacy.ExtractAndProcessParameters(
            new byte[VocoderFrameSizes.HalfRateCodewordBytes],
            parameters);

        Assert.True(status.Unrecoverable);
        Assert.Equal(15u, status.DecoderErrorMetric);
    }

    [Fact]
    public void AesNextMessageIndicatorMatchesReferenceLfsrVector()
    {
        byte[] initialMessageIndicator = Convert.FromHexString("12345678");
        using var privacy = new DmrPrivacyProcessor(
            new FakeHalfRateSession(),
            new DmrPrivacyOptions(
                DmrPrivacyAlgorithms.Aes256,
                keyId: 0x45,
                new byte[32],
                initialMessageIndicator));

        Assert.Equal(Convert.FromHexString("B451463A"), privacy.GetNextMessageIndicator());
        Assert.Equal(initialMessageIndicator, privacy.MessageIndicator.ToArray());
    }

    [Fact]
    public void EncryptedCallEmitsVoiceAndPrivacyHeadersBeforeVoice()
    {
        var packets = new List<byte[]>();
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Arc4,
            keyId: 1,
            Convert.FromHexString("0102030405"),
            Convert.FromHexString("12345678"));
        using var call = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeHalfRateSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: options);

        call.Start();
        call.Process(new short[480]);

        Assert.Equal(3, packets.Count);
        Assert.Equal((byte)0x21, packets[0][15]);
        Assert.Equal((byte)0x20, packets[1][15]);
        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(packets[1], out var metadata));
        Assert.Equal(DmrPrivacyAlgorithms.Arc4, metadata.AlgorithmId);
        Assert.Equal((byte)0x10, packets[2][15]);
        Assert.NotEqual(new byte[DmrVoicePacketCodec.AmbeBytes], DmrVoicePacketCodec.ExtractAmbe(packets[2]));
    }

    [Fact]
    public void EncryptedCallKeepsVoiceBurstsContiguousAfterStartupPrivacyHeader()
    {
        var packets = new List<byte[]>();
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Arc4,
            keyId: 1,
            Convert.FromHexString("0102030405"),
            Convert.FromHexString("12345678"));
        using var call = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeHalfRateSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: options);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 21]);

        Assert.Equal(9, packets.Count);
        Assert.Single(packets, packet => DmrVoicePacketCodec.IsPrivacyIndicator(packet));
        Assert.All(packets.Skip(2), packet => Assert.False(DmrVoicePacketCodec.IsPrivacyIndicator(packet)));
        Assert.Equal((byte)0x10, packets[8][15]);
    }

    [Fact]
    public void EncryptedSuperframeCarriesLateEntryMiAndBurstFIdentifiers()
    {
        byte[] initialMessageIndicator = Convert.FromHexString("12345678");
        var packets = new List<byte[]>();
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Aes256,
            keyId: 0x45,
            new byte[32],
            initialMessageIndicator);
        using var call = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeHalfRateSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: options);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 18]);

        byte[][] voicePackets = packets.Skip(2).ToArray();
        Assert.Equal(6, voicePackets.Length);
        var collector = new DmrLateEntryMessageIndicator();
        byte[] decodedMessageIndicator = [];
        for (byte burst = 0; burst < voicePackets.Length; burst++)
        {
            bool complete = collector.AddVoiceBurst(
                burst,
                DmrVoicePacketCodec.ExtractAmbe(voicePackets[burst]),
                out decodedMessageIndicator);
            Assert.Equal(burst == 5, complete);
        }
        Assert.Equal(Convert.FromHexString("B451463A"), decodedMessageIndicator);

        Assert.True(DmrVoicePacketCodec.TryExtractBurstFSignaling(voicePackets[^1], out var signaling));
        Assert.False(signaling.IsReverseChannel);
        Assert.Equal(DmrPrivacyAlgorithms.Aes256, signaling.AlgorithmId);
        Assert.Equal((byte)0x45, signaling.KeyId);
    }

    [Fact]
    public void EncryptedCallMarksEmbeddedGroupLinkControlWithAssociationFeatureSet()
    {
        var packets = new List<byte[]>();
        var options = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Aes256,
            keyId: 0x45,
            new byte[32],
            Convert.FromHexString("12345678"));
        using var call = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeHalfRateSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: options);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 18]);

        var embedded = new fnecore.DMR.EmbeddedData();
        foreach (byte[] packet in packets.Skip(3).Take(4))
        {
            byte[] frame = packet[DmrVoicePacketCodec.HeaderBytes..^2];
            var header = new fnecore.DMR.EMB();
            header.Decode(frame);
            embedded.AddData(ref frame, header.LCSS);
        }

        fnecore.DMR.LC linkControl = Assert.IsType<fnecore.DMR.LC>(embedded.GetLC());
        Assert.Equal(DmrPrivacyAlgorithms.FeatureId, linkControl.FID);
        Assert.True(linkControl.Encrypted);
        Assert.Equal(0x40, linkControl.GetBytes()[2] & 0x40);
    }

    [Fact]
    public void LateEntryMiMatchesIndependentGolayReferenceVector()
    {
        byte[,] expectedFragments =
        {
            { 0x1, 0x4, 0x7 },
            { 0x2, 0x5, 0x8 },
            { 0x3, 0x6, 0x9 },
            { 0x0, 0xB, 0x8 },
            { 0xA, 0x6, 0x1 },
            { 0xC, 0xC, 0x0 }
        };
        var encoder = new DmrLateEntryMessageIndicator(Convert.FromHexString("12345678"));

        for (int burst = 0; burst < 6; burst++)
        {
            for (int codeword = 0; codeword < 3; codeword++)
            {
                byte[] encoded = new byte[DmrVoicePacketCodec.CodewordBytes];
                encoder.ApplyFragment(encoded, burst, codeword);
                AssertC3PrefixWireEncoding(expectedFragments[burst, codeword], encoded);
            }
        }
    }

    [Fact]
    public void BurstFCodecKeepsReverseChannelDistinctFromSingleBurst()
    {
        var reverseChannel = new DmrBurstFSignaling(IsReverseChannel: true, Payload: 0x0A5);
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            voiceSync: false,
            embeddedSequence: 5,
            frameSequence: 5,
            ambe: new byte[DmrVoicePacketCodec.AmbeBytes],
            burstFSignaling: reverseChannel);

        Assert.True(DmrVoicePacketCodec.TryExtractBurstFSignaling(packet, out var decoded));
        Assert.True(decoded.IsReverseChannel);
        Assert.Equal(reverseChannel.Payload, decoded.Payload);
    }

    [Fact]
    public void BurstFEncryptionIdentifiersMatchEtsiInterleaveVector()
    {
        DmrBurstFSignaling identifiers = DmrBurstFSignaling.EncryptionIdentifiers(
            DmrPrivacyAlgorithms.Aes256,
            keyId: 0x45);
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            voiceSync: false,
            embeddedSequence: 5,
            frameSequence: 5,
            ambe: new byte[DmrVoicePacketCodec.AmbeBytes],
            burstFSignaling: identifiers);

        Assert.Equal(
            Convert.FromHexString("6437983B"),
            ReadBurstFMiddleBytes(packet));
    }

    private sealed class FakeHalfRateSession : IHalfRateVocoderSession
    {
        public int ExtractResult { get; init; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int FlushEncode(Span<byte> codeword) => 0;

        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters)
        {
            parameters.Clear();
            return parameters.Length;
        }

        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false) => 0;

        public int FlushEncodeParameters(Span<byte> parameters) => 0;

        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            codeword[..parameters.Length].CopyTo(parameters);
            return ExtractResult;
        }

        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
        {
            codeword.Clear();
            parameters.CopyTo(codeword);
        }

        public void Dispose()
        {
        }
    }

    private static void AssertC3PrefixWireEncoding(byte fragment, byte[] codeword)
    {
        byte[] expectedByte7ByLowBits = [0x00, 0x10, 0x01, 0x11];
        byte[] expectedByte8ByHighBits = [0x00, 0x10, 0x01, 0x11];

        Assert.All(codeword[..7], value => Assert.Equal((byte)0, value));
        Assert.Equal(expectedByte7ByLowBits[fragment & 0x03], codeword[7]);
        Assert.Equal(expectedByte8ByHighBits[fragment >> 2], codeword[8]);
    }

    private static byte[] ReadBurstFMiddleBytes(ReadOnlySpan<byte> packet)
    {
        ReadOnlySpan<byte> frame = packet.Slice(
            DmrVoicePacketCodec.HeaderBytes,
            DmrVoicePacketCodec.FrameBytes);
        byte[] result = new byte[4];
        for (int bit = 0; bit < 32; bit++)
        {
            int frameBit = 116 + bit;
            int value = frame[frameBit / 8] >> (7 - frameBit % 8) & 1;
            result[bit / 8] |= (byte)(value << (7 - bit % 8));
        }
        return result;
    }
}
