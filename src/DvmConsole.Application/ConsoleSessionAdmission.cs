// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Separates reversible replacement admission from permanent session retirement.</summary>
internal sealed class ConsoleSessionAdmission(SessionTerminalFence terminal)
{
    private int suspended;
    public bool IsSuppressed => Volatile.Read(ref suspended) != 0 || terminal.IsClosed;
    public void Suspend() => Volatile.Write(ref suspended, 1);
    public bool TryResume() => terminal.TryRun(() => Volatile.Write(ref suspended, 0));
    public void Close() => terminal.TryClose();
}
