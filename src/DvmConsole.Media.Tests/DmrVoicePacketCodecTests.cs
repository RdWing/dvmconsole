using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

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

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => codeword.Length;

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
