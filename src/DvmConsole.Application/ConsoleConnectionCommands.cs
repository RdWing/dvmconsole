// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Connection operations kept separate from channel and PTT commands.</summary>
public interface IConsoleConnectionCommands
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task ToggleAsync(SystemId systemId, CancellationToken cancellationToken = default);
    Task RestoreAsync(IEnumerable<SystemId> systemIds, CancellationToken cancellationToken = default);
}
