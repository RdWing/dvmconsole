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
    public void EncryptedCallUpdatesPrivacyHeaderBeforeSeventhVoicePacket()
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

        Assert.Equal(10, packets.Count);
        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(packets[8], out var updated));
        Assert.NotEqual(options.MessageIndicator.ToArray(), updated.MessageIndicator);
        Assert.Equal((byte)0x10, packets[9][15]);
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
}
