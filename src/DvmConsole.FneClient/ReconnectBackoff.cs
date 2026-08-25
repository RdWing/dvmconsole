namespace DvmConsole.FneClient;

/// <summary>
/// Produces a bounded exponential delay for consecutive FNE login attempts.
/// A successful connection or explicit operator restart resets the sequence.
/// </summary>
internal sealed class ReconnectBackoff
{
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(60);

    private readonly object sync = new();
    private int consecutiveAttempts;

    public TimeSpan NextDelay(TimeSpan normalRetryInterval)
    {
        if (normalRetryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(normalRetryInterval));

        lock (sync)
        {
            long maximumTicks = Math.Max(normalRetryInterval.Ticks, MaximumDelay.Ticks);
            int shift = Math.Min(consecutiveAttempts, 30);
            long delayTicks = normalRetryInterval.Ticks > (maximumTicks >> shift)
                ? maximumTicks
                : normalRetryInterval.Ticks << shift;
            if (consecutiveAttempts < 30)
                consecutiveAttempts++;

            return TimeSpan.FromTicks(Math.Min(delayTicks, maximumTicks));
        }
    }

    public void Reset()
    {
        lock (sync)
            consecutiveAttempts = 0;
    }
}
