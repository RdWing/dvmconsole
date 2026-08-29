namespace DvmConsole.Media;

// Keeps outbound media frames on an absolute 20 ms start cadence. A late timer
// wake is recovered against the next scheduled deadline instead of permanently
// slowing the stream. Falling a complete frame behind starts a new cadence so
// recovery can never emit an immediate catch-up burst.
public sealed class TransmitFrameCadence
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delay;
    private readonly long frameIntervalTimestampUnits;
    private readonly bool delayFirstFrame;
    private long nextFrameStartTimestamp;
    private bool frameStarted;

    public TransmitFrameCadence(
        TimeProvider? timeProvider = null,
        bool delayFirstFrame = false)
        : this(timeProvider, delay: null, delayFirstFrame)
    {
    }

    internal TransmitFrameCadence(
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, ValueTask>? delay,
        bool delayFirstFrame = false)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? DelayAsync;
        this.delayFirstFrame = delayFirstFrame;
        frameIntervalTimestampUnits = Math.Max(
            1,
            checked((long)Math.Round(
                FrameInterval.TotalSeconds * this.timeProvider.TimestampFrequency)));
    }

    public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!frameStarted)
        {
            long now = timeProvider.GetTimestamp();
            nextFrameStartTimestamp = AddFrameInterval(now);
            frameStarted = true;
            if (!delayFirstFrame)
                return;
        }

        long scheduledStart = nextFrameStartTimestamp;
        await DelayUntilAsync(scheduledStart, cancellationToken).ConfigureAwait(false);

        long actualStart = timeProvider.GetTimestamp();
        long followingScheduledStart = AddFrameInterval(scheduledStart);
        nextFrameStartTimestamp = HasReached(actualStart, followingScheduledStart)
            ? AddFrameInterval(actualStart)
            : followingScheduledStart;
    }

    private async ValueTask DelayUntilAsync(
        long scheduledStart,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long now = timeProvider.GetTimestamp();
            TimeSpan remaining = timeProvider.GetElapsedTime(now, scheduledStart);
            if (remaining <= TimeSpan.Zero)
                return;
            await delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private long AddFrameInterval(long timestamp)
        => unchecked(timestamp + frameIntervalTimestampUnits);

    private bool HasReached(long timestamp, long target)
        => timeProvider.GetElapsedTime(target, timestamp) >= TimeSpan.Zero;

    private async ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        => await Task.Delay(duration, timeProvider, cancellationToken).ConfigureAwait(false);
}
