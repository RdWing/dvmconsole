using DvmConsole.Audio;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class AnalogTransmitCaptureSessionTests
{
    [Fact]
    public void FramesPcmIntoVoiceStartAndVoicePackets()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new AnalogTxAudioSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)),
            grantDemand: true);

        session.Start();
        Assert.Equal(0, session.Process(new short[100]));
        Assert.Equal(2, session.Process(new short[220]));

        Assert.Equal(2, session.FramesSent);
        Assert.Equal(2, packets.Count);
        Assert.Equal((ushort)0, packets[0].Sequence);
        Assert.Equal((ushort)1, packets[1].Sequence);
        Assert.Equal(AnalogAudioFrameType.VoiceStart, (AnalogAudioFrameType)(packets[0].Payload[AnalogVoicePacketCodec.FrameTypeOffset] & 0x0F));
        Assert.Equal(AnalogAudioFrameType.Voice, (AnalogAudioFrameType)(packets[1].Payload[AnalogVoicePacketCodec.FrameTypeOffset] & 0x0F));
        Assert.Equal((byte)0x80, packets[0].Payload[AnalogVoicePacketCodec.ControlOffset]);
        Assert.Equal((byte)0, packets[1].Payload[AnalogVoicePacketCodec.ControlOffset]);
    }

    [Fact]
    public async Task CaptureStartsWithoutSendingAndEndsWithRtpCallEndTerminator()
    {
        var capture = new FakeCapture();
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        await using var session = new AnalogTransmitCaptureSession(
            capture,
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        await session.StartAsync();

        Assert.True(session.IsRunning);
        Assert.Empty(packets);
        capture.Emit(new short[160]);
        Assert.Single(packets);

        await session.StopAsync();

        Assert.False(session.IsRunning);
        Assert.Equal(2, packets.Count);
        Assert.Equal(ushort.MaxValue, packets[^1].Sequence);
        Assert.Equal(AnalogAudioFrameType.Terminator, (AnalogAudioFrameType)(packets[^1].Payload[AnalogVoicePacketCodec.FrameTypeOffset] & 0x0F));
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
}
