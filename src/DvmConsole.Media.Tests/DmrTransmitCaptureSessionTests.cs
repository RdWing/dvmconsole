using DvmConsole.Audio;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrTransmitCaptureSessionTests
{
    [Fact]
    public async Task StartsCaptureAfterCallHeaderAndEndsWithTerminator()
    {
        var capture = new FakeCapture();
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        await using var session = new DmrTransmitCaptureSession(
            capture,
            new FakeVocoderSession(),
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        await session.StartAsync();

        Assert.True(session.IsRunning);
        Assert.True(capture.IsRunning);
        Assert.Single(packets);

        capture.Emit(new short[480]);
        Assert.Equal(2, packets.Count);

        await session.StopAsync();

        Assert.False(session.IsRunning);
        Assert.False(capture.IsRunning);
        Assert.Equal(3, packets.Count);
        Assert.Equal((byte)0xA2, packets[^1].Payload[15]);
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void Emit(short[] samples)
        {
            if (IsRunning)
                SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
        }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }
}
