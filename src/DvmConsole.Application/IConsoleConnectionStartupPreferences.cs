// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
namespace DvmConsole.Application;

/// <summary>Device-local, configuration-scoped connection intent for the next session.</summary>
public interface IConsoleConnectionStartupPreferences
{
    ValueTask<bool> LoadAutoConnectAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAutoConnectAsync(bool enabled, CancellationToken cancellationToken = default);
}
