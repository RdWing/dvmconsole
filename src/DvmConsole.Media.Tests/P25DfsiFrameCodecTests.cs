using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25DfsiFrameCodecTests
{
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

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
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
