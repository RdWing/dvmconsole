using DvmConsole.Operations;

namespace DvmConsole.Application;

// A small, allocation-free observer for the receive hot path. Percentiles are
// reported as the upper bound of the fixed bucket containing the percentile.
internal sealed class FixedBucketLatencyTracker
{
    private static readonly long[] BucketUpperBoundsTicks =
    [
        TimeSpan.FromMilliseconds(1).Ticks,
        TimeSpan.FromMilliseconds(2).Ticks,
        TimeSpan.FromMilliseconds(4).Ticks,
        TimeSpan.FromMilliseconds(8).Ticks,
        TimeSpan.FromMilliseconds(16).Ticks,
        TimeSpan.FromMilliseconds(32).Ticks,
        TimeSpan.FromMilliseconds(64).Ticks,
        TimeSpan.FromMilliseconds(128).Ticks,
        TimeSpan.FromMilliseconds(256).Ticks,
        TimeSpan.FromMilliseconds(512).Ticks,
        TimeSpan.FromSeconds(1).Ticks,
        TimeSpan.FromSeconds(2).Ticks,
        TimeSpan.FromSeconds(5).Ticks,
        TimeSpan.MaxValue.Ticks
    ];

    private readonly long[] buckets = new long[BucketUpperBoundsTicks.Length];
    private long maximumObservedTicks;

    public void Observe(TimeSpan latency)
    {
        long ticks = Math.Max(0, latency.Ticks);
        int bucket = Array.BinarySearch(BucketUpperBoundsTicks, ticks);
        if (bucket < 0)
            bucket = ~bucket;
        Interlocked.Increment(ref buckets[Math.Min(bucket, buckets.Length - 1)]);
        ObserveMaximum(ticks);
    }

    public LatencyPercentiles Snapshot()
    {
        long[] counts = new long[buckets.Length];
        long total = 0;
        for (int index = 0; index < buckets.Length; index++)
        {
            counts[index] = Interlocked.Read(ref buckets[index]);
            total = SaturatingAdd(total, counts[index]);
        }

        if (total == 0)
            return new LatencyPercentiles(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        return new LatencyPercentiles(
            Resolve(counts, total, 50),
            Resolve(counts, total, 95),
            Resolve(counts, total, 99));
    }

    private TimeSpan Resolve(long[] counts, long total, int percentile)
    {
        long target = Math.Max(1, (long)Math.Ceiling(total * percentile / 100d));
        long cumulative = 0;
        for (int index = 0; index < counts.Length; index++)
        {
            cumulative = SaturatingAdd(cumulative, counts[index]);
            if (cumulative >= target)
            {
                long upperBound = BucketUpperBoundsTicks[index];
                return upperBound == TimeSpan.MaxValue.Ticks
                    ? TimeSpan.FromTicks(Math.Max(
                        TimeSpan.FromSeconds(5).Ticks,
                        Interlocked.Read(ref maximumObservedTicks)))
                    : TimeSpan.FromTicks(upperBound);
            }
        }

        return TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(5).Ticks,
            Interlocked.Read(ref maximumObservedTicks)));
    }

    private void ObserveMaximum(long ticks)
    {
        long current = Interlocked.Read(ref maximumObservedTicks);
        while (ticks > current)
        {
            long observed = Interlocked.CompareExchange(
                ref maximumObservedTicks,
                ticks,
                current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
