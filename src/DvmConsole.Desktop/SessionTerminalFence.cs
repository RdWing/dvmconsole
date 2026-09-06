// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

// One-way generation fence for session work that outlives a bounded shutdown.
// Once closed, no late recovery or presentation continuation may publish new
// state or reopen an operator-facing route for this session.
internal sealed class SessionTerminalFence
{
    private readonly object sync = new();
    private bool closed;

    public bool IsClosed
    {
        get
        {
            lock (sync)
                return closed;
        }
    }

    public bool TryClose()
    {
        lock (sync)
        {
            if (closed)
                return false;
            closed = true;
            return true;
        }
    }

    public bool TryRun(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (sync)
        {
            if (closed)
                return false;
            action();
            return true;
        }
    }
}
