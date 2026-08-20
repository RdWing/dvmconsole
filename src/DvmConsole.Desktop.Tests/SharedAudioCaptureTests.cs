using DvmConsole.Audio;
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
        Task ready = shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));
        Assert.False(ready.IsCompleted);

        source.Emit([]);
        Assert.False(ready.IsCompleted);
        source.Emit([1, 2, 3]);

        await ready;
        Assert.Empty(published);
    }

    [Fact]
    public async Task CanRequireSustainedPhysicalSamplesBeforeReportingReady()
    {
        var source = new FakeCapture();
        await using var shared = new SharedAudioCapture(
            source,
            TimeSpan.FromMilliseconds(50));
        await using SharedAudioCapture.Lease lease = shared.CreateLease();
        var published = new List<short[]>();
        lease.SamplesAvailable += (_, args) => published.Add(args.Samples.ToArray());

        shared.SetSamplesSuppressed(true);
        await lease.StartAsync();
        Task ready = shared.WaitForSamplesAsync(TimeSpan.FromSeconds(1));

        source.Emit(new short[160]);
        source.Emit(new short[160]);
        Assert.False(ready.IsCompleted);
        source.Emit(new short[80]);

        await ready;
        Assert.Empty(published);
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
}
