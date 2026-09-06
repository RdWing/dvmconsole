// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
namespace DvmConsole.Desktop;

// A burst shares one queued callback. Explicit flushes claim the same pending
// work, so an already queued callback cannot replay an older settings snapshot.
internal sealed class CoalescedUiAction(IUiDispatcher dispatcher, Action action,
    Action<Exception>? reportFailure = null) : IDisposable
{
    private int pending;
    private int disposed;

    public void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref pending, 1) != 0)
            return;
        dispatcher.Post(() =>
        {
            if (Volatile.Read(ref disposed) != 0)
                return;
            try { RunPending(); }
            catch (Exception exception) when (reportFailure is not null) { reportFailure(exception); }
        });
    }

    public ValueTask FlushAsync() => dispatcher.InvokeAsync(RunPending);

    private void RunPending()
    {
        if (Interlocked.Exchange(ref pending, 0) != 0)
            action();
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
