using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

// Keeps diagnostic ingestion from monopolizing the UI dispatcher during
// sustained traffic. Producers may run on any thread; published batches always
// run through the supplied UI scheduler.
internal sealed class DebugLogDrainController
{
    internal const int DefaultMaximumPendingEntries = 2_048;
    internal const int DefaultMaximumBatchSize = 64;
    private readonly object sync = new();
    private readonly Queue<DebugLogEntry> pending = [];
    private readonly Func<bool> hasUiThreadAccess;
    private readonly Action<Action> postToUiThread;
    private readonly Action<IReadOnlyList<DebugLogEntry>> publishBatch;
    private readonly Func<bool> isStopped;
    private readonly Func<DateTimeOffset> getNow;
    private readonly int maximumPendingEntries;
    private readonly int maximumBatchSize;
    private bool drainScheduled;
    private long discardedSinceLastDrain;

    public DebugLogDrainController(
        Func<bool> hasUiThreadAccess,
        Action<Action> postToUiThread,
        Action<IReadOnlyList<DebugLogEntry>> publishBatch,
        Func<bool>? isStopped = null,
        Func<DateTimeOffset>? getNow = null,
        int maximumPendingEntries = DefaultMaximumPendingEntries,
        int maximumBatchSize = DefaultMaximumBatchSize)
    {
        this.hasUiThreadAccess = hasUiThreadAccess ?? throw new ArgumentNullException(nameof(hasUiThreadAccess));
        this.postToUiThread = postToUiThread ?? throw new ArgumentNullException(nameof(postToUiThread));
        this.publishBatch = publishBatch ?? throw new ArgumentNullException(nameof(publishBatch));
        this.isStopped = isStopped ?? (() => false);
        this.getNow = getNow ?? (() => DateTimeOffset.Now);
        if (maximumPendingEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingEntries));
        if (maximumBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        this.maximumPendingEntries = maximumPendingEntries;
        this.maximumBatchSize = maximumBatchSize;
    }

    public void Enqueue(DebugLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (isStopped())
            return;

        bool scheduleDrain = false;
        lock (sync)
        {
            if (pending.Count >= maximumPendingEntries)
            {
                if (entry.Severity < DebugLogSeverity.Warning)
                {
                    discardedSinceLastDrain = SaturatingIncrement(discardedSinceLastDrain);
                    return;
                }

                pending.Dequeue();
                discardedSinceLastDrain = SaturatingIncrement(discardedSinceLastDrain);
            }

            pending.Enqueue(entry);
            if (!drainScheduled)
            {
                drainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (!scheduleDrain)
            return;

        if (hasUiThreadAccess())
            Drain();
        else
            postToUiThread(Drain);
    }

    private void Drain()
    {
        if (isStopped())
        {
            lock (sync)
            {
                pending.Clear();
                drainScheduled = false;
                discardedSinceLastDrain = 0;
            }
            return;
        }

        List<DebugLogEntry> batch;
        long discarded;
        lock (sync)
        {
            int batchSize = Math.Min(maximumBatchSize, pending.Count);
            batch = new List<DebugLogEntry>(batchSize + 1);
            for (int index = 0; index < batchSize; index++)
                batch.Add(pending.Dequeue());

            discarded = discardedSinceLastDrain;
            discardedSinceLastDrain = 0;
        }

        if (discarded > 0)
        {
            batch.Add(new DebugLogEntry(
                getNow(),
                "LOG",
                DebugLogSeverity.Warning,
                $"Discarded {discarded:N0} pending diagnostic log entries to keep the operator UI responsive."));
        }

        try
        {
            if (batch.Count > 0)
                publishBatch(batch);
        }
        finally
        {
            bool scheduleNextDrain;
            lock (sync)
            {
                scheduleNextDrain = pending.Count > 0;
                if (!scheduleNextDrain)
                    drainScheduled = false;
            }

            if (scheduleNextDrain)
                postToUiThread(Drain);
        }
    }

    private static long SaturatingIncrement(long value)
        => value == long.MaxValue ? long.MaxValue : value + 1;
}
