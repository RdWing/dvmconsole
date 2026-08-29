using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class TransmitFrameCadenceTests
{
    [Fact]
    public async Task ProcessingTimeIsSubtractedFromTheNextFrameDelay()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(4));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(7));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal(
            [TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(13)],
            delays);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);

        ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            time.Advance(duration);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SubFrameLatenessUsesTheNextAbsoluteDeadlineWithoutBursting()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(25));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(2));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(13)], delays);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);

        ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            time.Advance(duration);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WholeFrameLatenessStartsANewCadenceWithoutAnImmediateCatchUpBurst()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(45));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(2));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(18)], delays);
        Assert.Equal(TimeSpan.FromMilliseconds(65), time.Elapsed);

        ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            time.Advance(duration);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RepeatedTimerOvershootDoesNotAccumulateIntoEveryFrameInterval()
    {
        var time = new ManualTimeProvider();
        var frameStarts = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync);

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

        ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Advance(duration + TimeSpan.FromMilliseconds(11));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task OptionalFirstFrameDelayUsesTheSameAbsoluteCadence()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync, delayFirstFrame: true);

        await cadence.WaitForNextFrameAsync();
        await cadence.WaitForNextFrameAsync();

        Assert.Equal(
            [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)],
            delays);
        Assert.Equal(TimeSpan.FromMilliseconds(40), time.Elapsed);

        ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(duration);
            time.Advance(duration);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public TimeSpan Elapsed => TimeSpan.FromTicks(timestamp);

        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
