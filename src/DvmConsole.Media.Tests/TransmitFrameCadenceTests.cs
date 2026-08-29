using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class TransmitFrameCadenceTests
{
    [Fact]
    public async Task ProcessingTimeIsSubtractedFromTheNextFrameDelay()
    {
        var time = new ManualTimeProvider();
        var delay = new RecordingDelay(time);
        var cadence = new TransmitFrameCadence(time, delay.WaitAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(4));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(7));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal(
            [TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(13)],
            delay.Durations);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);
    }

    [Fact]
    public async Task SubFrameLatenessUsesTheNextAbsoluteDeadlineWithoutBursting()
    {
        var time = new ManualTimeProvider();
        var delay = new RecordingDelay(time);
        var cadence = new TransmitFrameCadence(time, delay.WaitAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(25));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(2));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(13)], delay.Durations);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);
    }

    [Fact]
    public async Task WholeFrameLatenessStartsANewCadenceWithoutAnImmediateCatchUpBurst()
    {
        var time = new ManualTimeProvider();
        var delay = new RecordingDelay(time);
        var cadence = new TransmitFrameCadence(time, delay.WaitAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(45));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(2));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(18)], delay.Durations);
        Assert.Equal(TimeSpan.FromMilliseconds(65), time.Elapsed);
    }

    [Fact]
    public async Task RepeatedTimerOvershootDoesNotAccumulateIntoEveryFrameInterval()
    {
        var time = new ManualTimeProvider();
        var frameStarts = new List<TimeSpan>();
        var delay = new RecordingDelay(time, TimeSpan.FromMilliseconds(11));
        var cadence = new TransmitFrameCadence(time, delay.WaitAsync);

        for (int frame = 0; frame < 10; frame++)
        {
            await cadence.WaitForNextFrameAsync();
            frameStarts.Add(time.Elapsed);
        }

        Assert.Equal(TimeSpan.Zero, frameStarts[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(31), frameStarts[1]);
        Assert.All(
            frameStarts.Zip(frameStarts.Skip(1), (first, second) => second - first).Skip(1),
            interval => Assert.Equal(TimeSpan.FromMilliseconds(20), interval));
        Assert.Equal(TimeSpan.FromMilliseconds(191), frameStarts[^1]);
    }

    [Fact]
    public async Task DelayedStartFactoryUsesTheSameAbsoluteCadence()
    {
        var time = new ManualTimeProvider();
        var delay = new RecordingDelay(time);
        TransmitFrameCadence cadence =
            TransmitFrameCadence.StartAfterFrameInterval(time, delay.WaitAsync);

        await cadence.WaitForNextFrameAsync();
        await cadence.WaitForNextFrameAsync();

        Assert.Equal(
            [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)],
            delay.Durations);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);
    }

    [Fact]
    public async Task TimestampConversionUsesTheTimeProviderFrequency()
    {
        const long timestampFrequency = 1001;
        var time = new ManualTimeProvider(timestampFrequency);
        var delay = new RecordingDelay(time);
        var cadence = new TransmitFrameCadence(time, delay.WaitAsync);

        await cadence.WaitForNextFrameAsync();
        await cadence.WaitForNextFrameAsync();

        Assert.Equal(20, time.Timestamp);
        Assert.Single(delay.Durations);
        Assert.Equal(
            TimeSpan.FromSeconds(20d / timestampFrequency),
            delay.Durations[0]);
    }

    private sealed class RecordingDelay(
        ManualTimeProvider time,
        TimeSpan? overshoot = null)
    {
        public List<TimeSpan> Durations { get; } = [];

        public ValueTask WaitAsync(
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Durations.Add(duration);
            time.Advance(duration + (overshoot ?? TimeSpan.Zero));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(
        long timestampFrequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency { get; } = timestampFrequency;
        public override long GetTimestamp() => timestamp;
        public long Timestamp => timestamp;
        public TimeSpan Elapsed => GetElapsedTime(0, timestamp);

        public void Advance(TimeSpan duration)
            => timestamp += checked((long)Math.Round(
                duration.TotalSeconds * TimestampFrequency));
    }
}
