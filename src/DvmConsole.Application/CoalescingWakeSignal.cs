// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

// A state change only needs to wake the single channel worker once. Keeping at
// most one pending signal prevents a burst of already-processed frames from
// turning into stale, immediate wakeups at a later jitter-buffer deadline.
internal sealed class CoalescingWakeSignal : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int pending;

    public bool Set()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0)
            return false;

        signal.Release();
        return true;
    }

    public async ValueTask<bool> WaitAsync(TimeSpan timeout)
    {
        bool signaled;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            signaled = true;
        }
        else
        {
            // SemaphoreSlim truncates TimeSpan to whole milliseconds. Rounding up
            // prevents a positive sub-millisecond deadline becoming a busy poll.
            TimeSpan wait = timeout > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Math.Ceiling(timeout.TotalMilliseconds))
                : timeout;
            signaled = await signal.WaitAsync(wait).ConfigureAwait(false);
        }

        if (signaled)
        {
            // A Set racing this reset is still observed by the worker's state
            // recheck; a later Set publishes the next binary signal normally.
            Volatile.Write(ref pending, 0);
        }
        return signaled;
    }

    internal bool TryConsume()
    {
        if (!signal.Wait(0))
            return false;

        Volatile.Write(ref pending, 0);
        return true;
    }

    public void Dispose()
        => signal.Dispose();
}
