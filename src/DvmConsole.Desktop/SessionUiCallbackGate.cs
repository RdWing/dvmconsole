// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

// Prevents work queued by a session-owned service from reaching the UI after
// that session has begun disposal. Close waits for an executing callback, so
// callback code and session resource disposal cannot overlap.
internal sealed class SessionUiCallbackGate
{
    private readonly object sync = new();
    private readonly IUiDispatcher dispatcher;
    private bool closed;

    public SessionUiCallbackGate(IUiDispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Post(Action callback, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // Producers must not wait for an executing UI callback. The callback
        // rechecks under the lifetime lock, so a concurrent Close still fences it.
        if (Volatile.Read(ref closed))
            return;

        dispatcher.Post(
            () =>
            {
                lock (sync)
                {
                    if (closed)
                        return;

                    callback();
                }
            },
            background);
    }

    public void Close()
    {
        lock (sync)
            Volatile.Write(ref closed, true);
    }
}
