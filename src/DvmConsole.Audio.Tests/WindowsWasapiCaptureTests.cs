using DvmConsole.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class WindowsWasapiCaptureTests
{
    [Fact]
    public void CopiesArbitraryCompletePcm16Packets()
    {
        short[] packet = [1, -2, 300, short.MinValue, short.MaxValue];

        short[] copied = WindowsWasapiCapture.CopyPcm16Samples(
            MemoryMarshal.AsBytes(packet.AsSpan()));

        Assert.Equal(packet, copied);
        Assert.NotSame(packet, copied);
    }

    [Fact]
    public void PreservesZeroedSilentPackets()
    {
        byte[] silentPacket = new byte[14];

        short[] copied = WindowsWasapiCapture.CopyPcm16Samples(silentPacket);

        Assert.Equal(7, copied.Length);
        Assert.All(copied, sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void RejectsIncompletePcm16Samples()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsWasapiCapture.CopyPcm16Samples(new byte[3]));
    }

    [Fact]
    public async Task StartAndStopAreIdempotentAndStopAwaitsTheNativeEvent()
    {
        var recorder = new TestWasapiRecorder();
        await using var capture = new WindowsWasapiCapture(
            recorder,
            PcmAudioFormat.Voice8KhzMono16Bit);

        await capture.StartAsync();
        await capture.StartAsync();
        Task stopping = capture.StopAsync().AsTask();

        Assert.Equal(1, recorder.StartCount);
        Assert.Equal(1, recorder.StopCount);
        Assert.False(stopping.IsCompleted);

        recorder.RaiseStopped();
        await stopping;
        await capture.StopAsync();

        Assert.False(capture.IsRunning);
        Assert.Equal(1, recorder.StopCount);
    }

    [Fact]
    public async Task UnexpectedStopFailureIsSurfacedByLaterLifecycleOperations()
    {
        var recorder = new TestWasapiRecorder();
        await using var capture = new WindowsWasapiCapture(
            recorder,
            PcmAudioFormat.Voice8KhzMono16Bit);
        var failure = new InvalidOperationException("endpoint failed");

        await capture.StartAsync();
        recorder.RaiseStopped(failure);

        Assert.False(capture.IsRunning);
        IOException exception = await Assert.ThrowsAsync<IOException>(() => capture.StartAsync().AsTask());
        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task DisposeAwaitsStopAndSuppressesLaterCallbacks()
    {
        var recorder = new TestWasapiRecorder();
        var capture = new WindowsWasapiCapture(
            recorder,
            PcmAudioFormat.Voice8KhzMono16Bit);
        int callbackCount = 0;
        capture.SamplesAvailable += (_, _) => callbackCount++;
        await capture.StartAsync();
        recorder.Emit([1, 2, 3]);

        Task disposing = capture.DisposeAsync().AsTask();
        recorder.Emit([4, 5, 6]);

        Assert.False(disposing.IsCompleted);
        Assert.Equal(1, callbackCount);

        recorder.RaiseStopped();
        await disposing;
        recorder.Emit([7, 8, 9]);

        Assert.Equal(1, callbackCount);
        Assert.Equal(1, recorder.DisposeCount);
    }

    private sealed class TestWasapiRecorder : IWindowsWasapiRecorder
    {
        public event CaptureDataAvailableHandler? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void StartRecording() => StartCount++;
        public void StopRecording() => StopCount++;

        public void Emit(ReadOnlySpan<short> samples)
            => DataAvailable?.Invoke(
                MemoryMarshal.AsBytes(samples),
                AudioClientBufferFlags.None,
                0,
                0);

        public void RaiseStopped(Exception? exception = null)
            => RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
