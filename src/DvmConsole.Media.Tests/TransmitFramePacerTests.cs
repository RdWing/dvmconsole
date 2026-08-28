using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class TransmitFramePacerTests
{
    [Fact]
    public async Task DefaultBacklogIsLimitedToOneSecondOfPacedAudio()
    {
        var pacer = new TransmitFramePacer(_ => { }, _ => { });

        Assert.Equal(50, pacer.Capacity);

        pacer.Complete();
        await pacer.Completion;
    }

    [Fact]
    public async Task LargeCaptureCallbackCannotBurstTwoP25Ldus()
    {
        var packets = new ConcurrentQueue<byte[]>();
        using var call = new P25TxCallSession(
            sourceId: 1,
            destinationId: 747,
            streamId: 3,
            new FakeVocoderSession(),
            (payload, _, _) => packets.Enqueue(payload.ToArray()));
        var cadence = new ManualCadence();
        int processedFrames = 0;
        var pacer = new TransmitFramePacer(
            samples =>
            {
                call.Process(samples);
                Interlocked.Increment(ref processedFrames);
            },
            exception => throw new Xunit.Sdk.XunitException(exception.Message),
            cadence.WaitAsync);

        call.Start();
        Assert.True(pacer.Enqueue(new short[18 * 160]));
        await WaitUntilAsync(() => Volatile.Read(ref processedFrames) == 1);
        Assert.Single(packets);

        for (int frameNumber = 2; frameNumber <= 18; frameNumber++)
        {
            await WaitUntilAsync(() => cadence.WaitCount == frameNumber - 1);
            Assert.Equal(frameNumber - 1, Volatile.Read(ref processedFrames));
            cadence.Release();
            await WaitUntilAsync(() => Volatile.Read(ref processedFrames) == frameNumber);
            Assert.Equal(
                frameNumber < 9 ? 1 : frameNumber < 18 ? 2 : 3,
                packets.Count);
        }

        pacer.Complete();
        await pacer.Completion;
        await call.EndAsync(static _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(4, packets.Count);
        Assert.Null(pacer.Failure);
    }

    [Fact]
    public async Task CompletionPreservesAPartialFinalFrame()
    {
        var processed = new ConcurrentQueue<short[]>();
        var cadence = new ManualCadence();
        var pacer = new TransmitFramePacer(
            samples => processed.Enqueue(samples.ToArray()),
            _ => { },
            cadence.WaitAsync);

        Assert.True(pacer.Enqueue(new short[200]));
        pacer.Complete();

        await WaitUntilAsync(() => processed.Count == 1);
        await WaitUntilAsync(() => cadence.WaitCount == 1);
        cadence.Release();
        await pacer.Completion;

        Assert.Equal([160, 40], processed.Select(frame => frame.Length));
        Assert.Null(pacer.Failure);
    }

    [Fact]
    public async Task BacklogLimitFailsClosedAndPublishesQueueHealth()
    {
        var cadence = new ManualCadence();
        var faults = new ConcurrentQueue<Exception>();
        var pacer = new TransmitFramePacer(
            _ => { },
            faults.Enqueue,
            cadence.WaitAsync,
            capacity: 1);

        Assert.False(pacer.Enqueue(new short[VocoderFrameSizes.PcmSamplesPerFrame * 20]));
        await pacer.Completion;

        InvalidOperationException failure = Assert.IsType<InvalidOperationException>(pacer.Failure);
        Assert.Contains("safety limit", failure.Message, StringComparison.Ordinal);
        Assert.Single(faults);
        Assert.Equal(1, pacer.CaptureHealth().Capacity);
        Assert.True(pacer.CaptureHealth().PeakDepth >= 1);
    }

    [Fact]
    public async Task CaptureLifecycleDrainsPacedFramesBeforeEndingCall()
    {
        var capture = new FakeCapture();
        var call = new RecordingCall();
        var cadence = new ManualCadence();
        var faults = new ConcurrentQueue<Exception>();
        await using var lifecycle = new TransmitCaptureLifecycle(
            capture,
            call,
            "The test capture session has faulted.",
            faults.Enqueue,
            cadence.WaitAsync);

        await lifecycle.StartAsync();
        lifecycle.Activate();
        capture.Emit(new short[320]);

        await WaitUntilAsync(() => call.Events.Count == 2);
        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Task stop = lifecycle.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.Equal(["start", "process:160"], call.Events);

        cadence.Release();
        await stop;

        Assert.Equal(["start", "process:160", "process:160", "end"], call.Events);
        Assert.Empty(faults);
    }

    [Fact]
    public async Task CaptureLifecycleSerializesConcurrentStartAndDisposal()
    {
        var capture = new FakeCapture();
        var call = new RecordingCall();
        var faults = new ConcurrentQueue<Exception>();
        var lifecycle = new TransmitCaptureLifecycle(
            capture,
            call,
            "The test capture session has faulted.",
            faults.Enqueue);

        await Task.WhenAll(
            lifecycle.StartAsync().AsTask(),
            lifecycle.StartAsync().AsTask());

        Assert.Equal(1, capture.StartCalls);
        await lifecycle.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.StartAsync().AsTask());

        await Task.WhenAll(
            lifecycle.DisposeAsync().AsTask(),
            lifecycle.DisposeAsync().AsTask());

        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(1, capture.DisposeCalls);
        Assert.Empty(faults);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class ManualCadence
    {
        private readonly SemaphoreSlim releases = new(0);
        private int waitCount;

        public int WaitCount => Volatile.Read(ref waitCount);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waitCount);
            await releases.WaitAsync(cancellationToken);
        }

        public void Release() => releases.Release();
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            IsRunning = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void Emit(short[] samples)
            => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }

    private sealed class RecordingCall : ITransmitCall
    {
        public List<string> Events { get; } = [];

        public void Start() => Events.Add("start");
        public void Process(ReadOnlySpan<short> samples) => Events.Add($"process:{samples.Length}");
        public ValueTask EndAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("end");
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Events.Add("dispose");
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { }
    }
}
