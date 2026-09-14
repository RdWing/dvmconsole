// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Explicit multi-select PTT, separate from a single channel's PTT gesture.</summary>
public interface IConsoleSelectedTransmitCommands
{
    bool IsSelectedPttRequested { get; }
    ValueTask<bool> BeginSelectedPttAsync(CancellationToken cancellationToken = default);
    ValueTask EndSelectedPttAsync(CancellationToken cancellationToken = default);
}
