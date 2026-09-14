// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Local listening choices, independent of transport and transmit admission.</summary>
public interface IConsoleListeningSettings
{
    bool OutputMuted { get; }
    bool CanSaveStartupPreference { get; }
    bool RestoreSelectedChannelsOnStartup { get; }
    ValueTask SetOutputMutedAsync(bool muted, CancellationToken cancellationToken = default);
    ValueTask SetRestoreSelectedChannelsAsync(bool restore, CancellationToken cancellationToken = default);
}

/// <summary>Host storage for the configuration-scoped startup choice.</summary>
public interface IConsoleListeningStartupPreferences
{
    ValueTask<bool> LoadRestoreSelectedChannelsAsync(CancellationToken cancellationToken);
    ValueTask SaveRestoreSelectedChannelsAsync(bool restore, CancellationToken cancellationToken);
}
