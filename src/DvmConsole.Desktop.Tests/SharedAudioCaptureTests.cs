using DvmConsole.Audio;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SharedAudioCaptureTests
{
    [Fact]
    public async Task KeepsOneMicrophoneOpenWhileMultipleTransmitLeasesRun()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(source);
        await using SharedAudioCapture.Lease first = shared.CreateLease();
        await using SharedAudioCapture.Lease second = shared.CreateLease();
        var firstFrames = new List<short[]>();
        var secondFrames = new List<short[]>();
        first.SamplesAvailable += (_, args) => firstFrames.Add(args.Samples.ToArray());
        second.SamplesAvailable += (_, args) => secondFrames.Add(args.Samples.ToArray());

        await first.StartAsync();
        await second.StartAsync();
        Assert.Equal(1, source.StartCalls);

        source.Emit([1, 2, 3]);
        Assert.Single(firstFrames);
        Assert.Single(secondFrames);

        await first.StopAsync();
        Assert.Equal(0, source.StopCalls);
        source.Emit([4]);
        Assert.Single(firstFrames);
        Assert.Equal(2, secondFrames.Count);

        await second.StopAsync();
        Assert.Equal(1, source.StopCalls);
    }

    [Fact]
    public async Task ReportsMicrophoneReadyFromFirstPhysicalSampleEvenWhenSuppressed()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(source);
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        var published = new List<short[]>();
        lease.SamplesAvailable += (_, args) => published.Add(args.Samples.ToArray());

        shared.SetSamplesSuppressed(true);
        await lease.StartAsync();
        Task<MicrophoneReadinessTiming> ready = shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));
        Assert.False(ready.IsCompleted);

        source.Emit([]);
        Assert.False(ready.IsCompleted);
        source.Emit([1, 2, 3]);

        MicrophoneReadinessTiming timing = await ready;
        Assert.Empty(published);
        Assert.True(timing.CaptureStartReturned >= TimeSpan.Zero);
        Assert.True(timing.FirstSamplesReceived >= timing.CaptureStartReturned);
    }

    [Fact]
    public async Task ConcurrentStartsDoNotLeaveALeaseRunningWhenPhysicalStartFails()
    {
        var source = new BlockingFailingCapture();
        await using var shared = new SharedAudioCapture(source);
        await using SharedAudioCapture.Lease first = shared.CreateLease();
        await using SharedAudioCapture.Lease second = shared.CreateLease();

        Task firstStart = first.StartAsync().AsTask();
        await source.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task secondStart = second.StartAsync().AsTask();

        Assert.False(secondStart.IsCompleted);
        source.FailStarts();
        await Assert.ThrowsAsync<IOException>(() => firstStart);
        await Assert.ThrowsAsync<IOException>(() => secondStart);

        Assert.False(first.IsRunning);
        Assert.False(second.IsRunning);
    }

    [Fact]
    public async Task SubscriberCanStopItsLeaseWithoutDeadlockingPublication()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(source);
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        lease.SamplesAvailable += (_, _) => lease.StopAsync().AsTask().GetAwaiter().GetResult();
        await lease.StartAsync();

        await Task.Run(() => source.Emit([1, 2, 3])).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(lease.IsRunning);
        Assert.Equal(1, source.StopCalls);
    }

    [Fact]
    public async Task ReadyCaptureBecomesStaleAndRequiresFreshSamples()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(
            source,
            staleAfter: TimeSpan.FromMilliseconds(20));
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        await lease.StartAsync();
        source.Emit([1, 2, 3]);
        await shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));

        Assert.True(shared.IsReady);
        long generation = shared.Health.CaptureGeneration;
        await Task.Delay(35);
        Assert.False(shared.IsReady);
        Assert.Equal(MicrophoneHealthState.Stale, shared.Health.State);

        Task<MicrophoneReadinessTiming> refreshed = shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));
        Assert.False(refreshed.IsCompleted);
        source.Emit([4, 5, 6]);
        await refreshed;

        Assert.True(shared.IsReady);
        Assert.Equal(generation, shared.Health.CaptureGeneration);
    }

    [Fact]
    public async Task PostTransitionGateRequiresANewPhysicalCallback()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(source);
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        shared.SetSamplesSuppressed(true);
        await lease.StartAsync();
        source.Emit([1, 2, 3]);
        await shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));

        Task<TimeSpan> resumed = shared.WaitForNextPhysicalSamplesAsync(TimeSpan.FromSeconds(1));
        Assert.False(resumed.IsCompleted);

        source.Emit([]);
        Assert.False(resumed.IsCompleted);
        source.Emit([4, 5, 6]);

        Assert.True(await resumed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task HealthAllowsObservedCallbackCadenceBeforeDeclaringCaptureStale()
    {
        var source = new FakeCapture();
        var time = new ManualTimeProvider();
        await using var shared = new SharedAudioCapture(
            source,
            staleAfter: TimeSpan.FromMilliseconds(50),
            timeProvider: time);
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        await lease.StartAsync();
        source.Emit([1]);
        await shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromMilliseconds(100));
        source.Emit([2]);

        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(MicrophoneHealthState.Ready, shared.Health.State);
        Assert.Equal(TimeSpan.FromMilliseconds(100), shared.Health.CallbackCadence);

        time.Advance(TimeSpan.FromMilliseconds(151));
        Assert.Equal(MicrophoneHealthState.Stale, shared.Health.State);
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            StopCalls++;
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

    private sealed class BlockingFailingCapture : IAudioCapture
    {
        private readonly TaskCompletionSource startFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable
        {
            add { }
            remove { }
        }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult();
            await startFailure.Task.WaitAsync(cancellationToken);
            throw new IOException("test capture start failure");
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void FailStarts() => startFailure.TrySetResult();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(timestamp);

        public void Advance(TimeSpan duration) => timestamp = checked(timestamp + duration.Ticks);
    }
}
