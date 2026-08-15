using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25DfsiFrameCodecTests
{
    [Fact]
    public void ClearTransmitPayloadsCarryExplicitUnencryptedMetadata()
    {
        byte[] ldu1 = P25DfsiFrameCodec.CreateLdu1Payload(99, 100, new byte[P25DfsiFrameCodec.ImbeBytes]);
        byte[] ldu2 = P25DfsiFrameCodec.CreateLdu2Payload(99, 100, new byte[P25DfsiFrameCodec.ImbeBytes]);

        Assert.Equal((byte)0x08, (byte)(ldu1[14] & 0x08));
        Assert.Equal(P25Defines.P25_FT_HDU_VALID, ldu1[180]);
        Assert.Equal(P25Defines.P25_ALGO_UNENCRYPT, ldu1[181]);
        Assert.Equal((byte)0x08, (byte)(ldu2[14] & 0x08));
        Assert.Equal(P25Defines.P25_ALGO_UNENCRYPT, ldu2[112]);
        Assert.Equal(P25Defines.P25_ALGO_UNENCRYPT, ldu2[181]);
    }

    [Fact]
    public async Task ZeroedLegacyClearHduMetadataDoesNotDisableReceiveAudio()
    {
        byte[] payload = P25DfsiFrameCodec.CreateLdu1Payload(
            99,
            100,
            new byte[P25DfsiFrameCodec.ImbeBytes]);
        payload[180] = P25Defines.P25_FT_HDU_VALID;
        payload[181] = 0;
        payload[182] = 0;
        payload[183] = 0;
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new P25RxAudioSession(
            new P25TrafficSelector(100),
            vocoder,
            playback);

        int errors = await session.ProcessAsync(CreateTraffic("LDU1", payload));

        Assert.Equal(0, errors);
        Assert.Equal(9, vocoder.Codewords.Count);
        Assert.Equal(9, playback.Frames.Count);
    }

    [Theory]
    [InlineData("LDU1", 0x62)]
    [InlineData("LDU2", 0x6B)]
    public void ExtractsNineImbeCodewordsFromDfsiRecords(string subtype, int firstRecordType)
    {
        byte[] payload = CreatePayload(firstRecordType);
        var traffic = CreateTraffic(subtype, payload);

        byte[] imbe = P25DfsiFrameCodec.ExtractImbe(traffic);

        Assert.Equal(P25DfsiFrameCodec.ImbeBytes, imbe.Length);
        for (int index = 0; index < P25DfsiFrameCodec.CodewordsPerLdu; index++)
        {
            byte expected = (byte)(index + 1);
            Assert.All(imbe.AsSpan(index * P25DfsiFrameCodec.CodewordBytes, P25DfsiFrameCodec.CodewordBytes).ToArray(), value => Assert.Equal(expected, value));
        }
    }

    [Fact]
    public async Task P25SessionDecodesNineImbeCodewordsToPlaybackFrames()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new P25RxAudioSession(new P25TrafficSelector(100), vocoder, playback);

        int errors = await session.ProcessAsync(CreateTraffic("LDU1", CreatePayload(0x62)));

        Assert.Equal(0, errors);
        Assert.Equal(9, session.FramesDecoded);
        Assert.Equal(9, vocoder.DecodeCalls);
        Assert.All(playback.Frames, frame => Assert.Equal(160, frame.Length));
    }

    [Fact]
    public async Task P25SessionIgnoresMalformedLduAndRecoversOnNextLdu()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new P25RxAudioSession(new P25TrafficSelector(100), vocoder, playback);

        Assert.Equal(
            0,
            await session.ProcessAsync(CreateTraffic("LDU1", new byte[P25DfsiFrameCodec.HeaderBytes])));
        Assert.Equal(0, session.FramesDecoded);

        Assert.Equal(0, await session.ProcessAsync(CreateTraffic("LDU1", CreatePayload(0x62))));
        Assert.Equal(9, session.FramesDecoded);
        Assert.Equal(9, playback.Frames.Count);
    }

    [Fact]
    public async Task P25SessionDecryptsEncryptedLdu1WithConfiguredKey()
    {
        const ushort keyId = 0x50;
        const byte algorithmId = P25Defines.P25_ALGO_AES;
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] messageIndicator = Enumerable.Range(0x10, 9).Select(static value => (byte)value).ToArray();
        byte[] clearImbe = Enumerable.Range(1, P25DfsiFrameCodec.ImbeBytes)
            .Select(static value => (byte)value)
            .ToArray();
        byte[] encryptedImbe = EncryptLdu(clearImbe, keyId, algorithmId, key, messageIndicator, P25DUID.LDU1);

        byte[] payload = P25DfsiFrameCodec.CreateLdu1Payload(99, 100, encryptedImbe);
        payload[P25DfsiFrameCodec.RecordLengthOffset] = (byte)P25DfsiFrameCodec.ClearLduPayloadLength;
        payload[180] = P25Defines.P25_FT_HDU_VALID;
        payload[181] = algorithmId;
        payload[182] = (byte)(keyId >> 8);
        payload[183] = (byte)keyId;
        messageIndicator.CopyTo(payload, 184);

        var resolver = new P25KeyRing(new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = keyId,
                    AlgId = algorithmId,
                    Key = Convert.ToHexString(key)
                }
            ]
        });
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new P25RxAudioSession(
            new P25TrafficSelector(100),
            vocoder,
            playback,
            resolver);

        int errors = await session.ProcessAsync(CreateTraffic("LDU1", payload));

        Assert.Equal(0, errors);
        Assert.Equal(9, vocoder.Codewords.Count);
        for (int index = 0; index < 9; index++)
        {
            Assert.Equal(
                clearImbe.AsSpan(index * P25DfsiFrameCodec.CodewordBytes, P25DfsiFrameCodec.CodewordBytes).ToArray(),
                vocoder.Codewords[index]);
        }
    }

    [Fact]
    public async Task P25SessionRejectsEncryptedTrafficWithoutConfiguredKey()
    {
        const ushort keyId = 0x50;
        const byte algorithmId = P25Defines.P25_ALGO_AES;
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] messageIndicator = Enumerable.Range(0x10, 9).Select(static value => (byte)value).ToArray();
        byte[] encryptedImbe = EncryptLdu(
            new byte[P25DfsiFrameCodec.ImbeBytes],
            keyId,
            algorithmId,
            key,
            messageIndicator,
            P25DUID.LDU1);
        byte[] payload = P25DfsiFrameCodec.CreateLdu1Payload(99, 100, encryptedImbe);
        payload[P25DfsiFrameCodec.RecordLengthOffset] = (byte)P25DfsiFrameCodec.ClearLduPayloadLength;
        payload[180] = P25Defines.P25_FT_HDU_VALID;
        payload[181] = algorithmId;
        payload[182] = (byte)(keyId >> 8);
        payload[183] = (byte)keyId;
        messageIndicator.CopyTo(payload, 184);

        var playback = new FakePlayback();
        await using var session = new P25RxAudioSession(
            new P25TrafficSelector(100),
            new FakeVocoderSession(),
            playback);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.ProcessAsync(CreateTraffic("LDU1", payload)));
        Assert.Empty(playback.Frames);
    }

    [Fact]
    public async Task P25SessionDecryptsLdu2UsingTheActiveKeyStream()
    {
        const ushort keyId = 0x50;
        const byte algorithmId = P25Defines.P25_ALGO_AES;
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        byte[] messageIndicator = Enumerable.Range(0x10, 9).Select(static value => (byte)value).ToArray();
        byte[] nextMessageIndicator = Enumerable.Range(0x30, 9).Select(static value => (byte)value).ToArray();
        byte[] clearLdu1 = Enumerable.Range(1, P25DfsiFrameCodec.ImbeBytes)
            .Select(static value => (byte)value)
            .ToArray();
        byte[] clearLdu2 = Enumerable.Range(101, P25DfsiFrameCodec.ImbeBytes)
            .Select(static value => (byte)value)
            .ToArray();

        var encryptor = new P25Crypto();
        encryptor.SetKey(keyId, algorithmId, key);
        Assert.True(encryptor.Prepare(algorithmId, keyId, messageIndicator));
        byte[] encryptedLdu1 = ProcessLdu(encryptor, clearLdu1, P25DUID.LDU1);
        byte[] encryptedLdu2 = ProcessLdu(encryptor, clearLdu2, P25DUID.LDU2);

        byte[] ldu1Payload = P25DfsiFrameCodec.CreateLdu1Payload(99, 100, encryptedLdu1);
        ldu1Payload[P25DfsiFrameCodec.RecordLengthOffset] = (byte)P25DfsiFrameCodec.ClearLduPayloadLength;
        ldu1Payload[180] = P25Defines.P25_FT_HDU_VALID;
        ldu1Payload[181] = algorithmId;
        ldu1Payload[182] = (byte)(keyId >> 8);
        ldu1Payload[183] = (byte)keyId;
        messageIndicator.CopyTo(ldu1Payload, 184);

        byte[] ldu2Payload = P25DfsiFrameCodec.CreateLdu2Payload(99, 100, encryptedLdu2);
        nextMessageIndicator.AsSpan(0, 3).CopyTo(ldu2Payload.AsSpan(61, 3));
        nextMessageIndicator.AsSpan(3, 3).CopyTo(ldu2Payload.AsSpan(78, 3));
        nextMessageIndicator.AsSpan(6, 3).CopyTo(ldu2Payload.AsSpan(95, 3));
        ldu2Payload[112] = algorithmId;
        ldu2Payload[113] = (byte)(keyId >> 8);
        ldu2Payload[114] = (byte)keyId;

        var resolver = new P25KeyRing(new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = keyId,
                    AlgId = algorithmId,
                    Key = Convert.ToHexString(key)
                }
            ]
        });
        var vocoder = new FakeVocoderSession();
        await using var session = new P25RxAudioSession(
            new P25TrafficSelector(100),
            vocoder,
            new FakePlayback(),
            resolver);

        Assert.Equal(0, await session.ProcessAsync(CreateTraffic("LDU1", ldu1Payload)));
        Assert.Equal(0, await session.ProcessAsync(CreateTraffic("LDU2", ldu2Payload)));

        Assert.Equal(18, vocoder.Codewords.Count);
        for (int index = 0; index < 9; index++)
        {
            Assert.Equal(
                clearLdu2.AsSpan(index * P25DfsiFrameCodec.CodewordBytes, P25DfsiFrameCodec.CodewordBytes).ToArray(),
                vocoder.Codewords[index + 9]);
        }
    }

    private static FneTrafficFrame CreateTraffic(string subtype, byte[] payload)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            subtype,
            1,
            99,
            payload);
    }

    private static byte[] CreatePayload(int firstRecordType)
    {
        int[] lengths = [22, 14, 17, 17, 17, 17, 17, 17, 16];
        int[] codewordOffsets = [10, 1, 5, 5, 5, 5, 5, 5, 4];
        byte[] payload = new byte[P25DfsiFrameCodec.HeaderBytes + P25DfsiFrameCodec.RecordBytes];
        payload[P25DfsiFrameCodec.RecordLengthOffset] = (byte)payload.Length;

        int offset = P25DfsiFrameCodec.HeaderBytes;
        for (int index = 0; index < lengths.Length; index++)
        {
            payload[offset] = (byte)(firstRecordType + index);
            byte value = (byte)(index + 1);
            for (int byteIndex = 0; byteIndex < P25DfsiFrameCodec.CodewordBytes; byteIndex++)
                payload[offset + codewordOffsets[index] + byteIndex] = value;
            offset += lengths[index];
        }

        return payload;
    }

    private static byte[] EncryptLdu(
        byte[] clearImbe,
        ushort keyId,
        byte algorithmId,
        byte[] key,
        byte[] messageIndicator,
        P25DUID duid)
    {
        var crypto = new P25Crypto();
        crypto.SetKey(keyId, algorithmId, key);
        Assert.True(crypto.Prepare(algorithmId, keyId, messageIndicator));
        return ProcessLdu(crypto, clearImbe, duid);
    }

    private static byte[] ProcessLdu(P25Crypto crypto, byte[] clearImbe, P25DUID duid)
    {
        byte[] encrypted = clearImbe.ToArray();
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

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }
        public List<byte[]> Codewords { get; } = [];
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            Codewords.Add(codeword.ToArray());
            samples.Fill((short)DecodeCalls);
            return 0;
        }

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
