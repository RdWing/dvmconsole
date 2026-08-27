namespace DvmConsole.Media;

// Keeps outbound media frames on their 20 ms start cadence. Encoding and
// transport work count toward the interval so their cost cannot accumulate
// into progressively later packets.
public sealed class TransmitFrameCadence
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delay;
    private long lastFrameStartedTimestamp;
    private bool frameStarted;

    public TransmitFrameCadence(TimeProvider? timeProvider = null)
        : this(timeProvider, delay: null)
    {
    }

    internal TransmitFrameCadence(
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, ValueTask>? delay)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? DelayAsync;
    }

    public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (frameStarted)
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(
                lastFrameStartedTimestamp,
                timeProvider.GetTimestamp());
            TimeSpan remaining = FrameInterval - elapsed;
            if (remaining > TimeSpan.Zero)
                await delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        lastFrameStartedTimestamp = timeProvider.GetTimestamp();
        frameStarted = true;
    }

    private async ValueTask DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        => await Task.Delay(duration, timeProvider, cancellationToken).ConfigureAwait(false);
}
