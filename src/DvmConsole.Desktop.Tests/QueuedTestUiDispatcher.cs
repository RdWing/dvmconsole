// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
using DvmConsole.Desktop;
namespace DvmConsole.Desktop.Tests;

internal sealed class QueuedTestUiDispatcher : IUiDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();
    public bool CheckAccess() => true;
    public void Post(Action action, bool background = false) => pending.Enqueue(action);
    public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
    public void RunPending() { while (pending.TryDequeue(out Action? action)) action(); }
}
