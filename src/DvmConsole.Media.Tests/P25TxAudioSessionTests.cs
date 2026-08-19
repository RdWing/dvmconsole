using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25TxAudioSessionTests
{
    [Fact]
    public void AggregatesExplicitGeneratedToneFramesWithoutPcmDetection()
    {
        var packets = new List<byte[]>();
        var vocoder = new FakeVocoderSession();
        using var session = new P25TxAudioSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: vocoder,
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        for (int frame = 0; frame < P25DfsiFrameCodec.CodewordsPerLdu; frame++)
            session.ProcessSingleTone(1000);

        Assert.Single(packets);
        byte[] extracted = P25DfsiFrameCodec.ExtractImbe(CreateTraffic("LDU1", packets[0]));
        Assert.Equal(
            Enumerable.Range(0, P25DfsiFrameCodec.CodewordsPerLdu)
                .SelectMany(_ => Enumerable.Repeat((byte)0xA5, P25DfsiFrameCodec.CodewordBytes)),
            extracted);
        Assert.All(vocoder.RequestedToneFrequencies, frequency => Assert.Equal(1000, frequency));
    }

    [Fact]
    public void RoutesBothQuickCallIiFrequenciesThroughGeneratedToneLookup()
    {
        var vocoder = new FakeVocoderSession();
        using var session = new P25TxAudioSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: vocoder,
            send: (_, _, _) => { });

        for (int frame = 0; frame < 50; frame++)
            session.ProcessSingleTone(600);
        for (int frame = 0; frame < 150; frame++)
            session.ProcessSingleTone(1200);

        Assert.Equal(200, vocoder.RequestedToneFrequencies.Count);
        Assert.All(vocoder.RequestedToneFrequencies.Take(50), frequency => Assert.Equal(600, frequency));
        Assert.All(vocoder.RequestedToneFrequencies.Skip(50), frequency => Assert.Equal(1200, frequency));
    }

    [Fact]
    public void CreatesRoundTrippableLdu1AndLdu2PayloadsFromPcmFrames()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new P25TxAudioSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        Assert.Equal(0, session.Process(new short[8 * 160]));
        Assert.Equal(2, session.Process(new short[10 * 160]));

        Assert.Equal(2, packets.Count);
        Assert.Equal((ushort)0, packets[0].Sequence);
        Assert.Equal((ushort)1, packets[1].Sequence);
        Assert.Equal((uint)77, packets[0].Stream);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[0].Payload[22]);
        Assert.Equal(P25DfsiFrameCodec.Ldu2Duid, packets[1].Payload[22]);

        Assert.Equal(
            Enumerable.Range(1, P25DfsiFrameCodec.ImbeBytes).Select(value => (byte)value),
            P25DfsiFrameCodec.ExtractImbe(CreateTraffic("LDU1", packets[0].Payload)));
        Assert.Equal(
            Enumerable.Range(100, P25DfsiFrameCodec.ImbeBytes).Select(value => (byte)value),
            P25DfsiFrameCodec.ExtractImbe(CreateTraffic("LDU2", packets[1].Payload)));
        Assert.Equal(18, session.CodewordsEncoded);
        Assert.Equal(2, session.LdusSent);
    }

    [Fact]
    public void EncryptsLdu1AndLdu2WithHduAndNextMessageIndicatorMetadata()
    {
        const byte algorithmId = P25Defines.P25_ALGO_AES;
        const ushort keyId = 0x50;
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] messageIndicator = Enumerable.Range(0x10, 9).Select(static value => (byte)value).ToArray();
        var encryption = new P25TxEncryptionOptions(algorithmId, keyId, key, messageIndicator);
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();

        using var session = new P25TxAudioSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)),
            encryption: encryption);

        Assert.Equal(2, session.Process(new short[18 * 160]));
        Assert.Equal(2, packets.Count);
        Assert.Equal((byte)0x08, (byte)(packets[0].Payload[14] & 0x08));
        Assert.Equal(P25DfsiFrameCodec.ClearLduPayloadLength, packets[0].Payload[P25DfsiFrameCodec.RecordLengthOffset]);

        var ldu1 = CreateTraffic("LDU1", packets[0].Payload);
        var ldu2 = CreateTraffic("LDU2", packets[1].Payload);
        Assert.True(P25DfsiFrameCodec.TryExtractEncryptionMetadata(ldu1, out P25DfsiFrameCodec.P25EncryptionMetadata firstMetadata));
        Assert.True(P25DfsiFrameCodec.TryExtractEncryptionMetadata(ldu2, out P25DfsiFrameCodec.P25EncryptionMetadata nextMetadata));
        Assert.Equal(algorithmId, firstMetadata.AlgorithmId);
        Assert.Equal(keyId, firstMetadata.KeyId);
        Assert.Equal(messageIndicator, firstMetadata.MessageIndicator);

        byte[] expectedNextMessageIndicator = messageIndicator.ToArray();
        P25Crypto.CycleP25Lfsr(expectedNextMessageIndicator);
        Assert.Equal(expectedNextMessageIndicator, nextMetadata.MessageIndicator);

        var decryptor = new P25Crypto();
        decryptor.SetKey(keyId, algorithmId, key);
        Assert.True(decryptor.Prepare(algorithmId, keyId, messageIndicator));
        byte[] clearLdu1 = DecryptLdu(decryptor, P25DfsiFrameCodec.ExtractImbe(ldu1), P25DUID.LDU1);
        byte[] clearLdu2 = DecryptLdu(decryptor, P25DfsiFrameCodec.ExtractImbe(ldu2), P25DUID.LDU2);

        Assert.Equal(
            Enumerable.Range(1, P25DfsiFrameCodec.ImbeBytes),
            clearLdu1.Select(value => (int)value));
        Assert.Equal(
            Enumerable.Range(100, P25DfsiFrameCodec.ImbeBytes),
            clearLdu2.Select(value => (int)value));
    }

    private static FneTrafficFrame CreateTraffic(string subtype, byte[] payload)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            2,
            3,
            null,
            "GROUP",
            "VOICE",
            subtype,
            1,
            77,
            payload);
    }

    private static byte[] DecryptLdu(P25Crypto crypto, byte[] encrypted, P25DUID duid)
    {
        for (int index = 0; index < P25DfsiFrameCodec.CodewordsPerLdu; index++)
        {
            byte[] codeword = encrypted
                .AsSpan(index * P25DfsiFrameCodec.CodewordBytes, P25DfsiFrameCodec.CodewordBytes)
                .ToArray();
            Assert.True(crypto.Process(codeword, duid));
            codeword.CopyTo(encrypted, index * P25DfsiFrameCodec.CodewordBytes);
        }

        return encrypted;
    }

    private sealed class FakeVocoderSession : IP25GeneratedToneVocoderSession
    {
        private byte nextValue = 1;
        public List<double> RequestedToneFrequencies { get; } = [];

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            for (int index = 0; index < codeword.Length; index++)
                codeword[index] = nextValue++;
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;

        public int EncodeSingleTone(double frequencyHz, Span<byte> codeword)
        {
            RequestedToneFrequencies.Add(frequencyHz);
            codeword.Fill(0xA5);
            return codeword.Length;
        }

        public void Dispose()
        {
        }
    }
}
