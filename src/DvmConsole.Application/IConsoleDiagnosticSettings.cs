// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Optional radio capability for operator-requested diagnostic detail.</summary>
public interface IRadioDiagnosticSettings
{
    void SetVerboseLogging(bool enabled);
}

public interface IConsoleDiagnosticSettings
{
    bool VerboseLoggingEnabled { get; }
    bool CanSaveDiagnosticSettings { get; }
    ValueTask SetVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IConsoleDiagnosticPreferences
{
    ValueTask<bool> LoadVerboseLoggingAsync(CancellationToken cancellationToken = default);
    ValueTask SaveVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default);
}
