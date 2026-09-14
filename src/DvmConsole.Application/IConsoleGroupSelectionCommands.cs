// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Adds saved group members to TX selection without starting a transmission.</summary>
public interface IConsoleGroupSelectionCommands
{
    Task<int> AddGroupToTransmitSelectionAsync(string name, CancellationToken cancellationToken = default);
}
