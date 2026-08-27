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
    public async Task LateFrameStartsANewCadenceWithoutAnImmediateCatchUpBurst()
    {
        var time = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var cadence = new TransmitFrameCadence(time, DelayAsync);

        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(25));
        await cadence.WaitForNextFrameAsync();
        time.Advance(TimeSpan.FromMilliseconds(2));
        await cadence.WaitForNextFrameAsync();

        Assert.Equal([TimeSpan.FromMilliseconds(18)], delays);
        Assert.Equal(TimeSpan.FromMilliseconds(45), time.Elapsed);

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
