// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal interface IConsoleSessionConnectionLifecycle
{
    IReadOnlyList<SystemId> CaptureActiveSystemIds();
    ValueTask QuiesceAsync(CancellationToken cancellationToken);
    ValueTask RestoreAsync(
        IReadOnlyList<SystemId> systemIds,
        CancellationToken cancellationToken);
}

internal sealed class ConsoleSessionConnectionLifecycle(
    MainWindowViewModel owner,
    Func<MainWindowViewModel, CancellationToken, Task> quiesceSession)
    : IConsoleSessionConnectionLifecycle
{
    public IReadOnlyList<SystemId> CaptureActiveSystemIds()
        => owner.CaptureActiveFneSystemIds();

    public ValueTask QuiesceAsync(CancellationToken cancellationToken)
        => new(quiesceSession(owner, cancellationToken));

    public ValueTask RestoreAsync(
        IReadOnlyList<SystemId> systemIds,
        CancellationToken cancellationToken)
        => new(owner.RestoreFneSessionAsync(systemIds, cancellationToken));
}
