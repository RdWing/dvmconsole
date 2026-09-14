// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleDiagnosticSettings
{
    private bool verboseLogging;
    public bool VerboseLoggingEnabled => Volatile.Read(ref verboseLogging);
    public bool CanSaveDiagnosticSettings => !IsStopping &&
        dependencies.Preferences is IConsoleDiagnosticPreferences &&
        radios.Sessions.Values.Any(radio => radio is IRadioDiagnosticSettings);

    private async Task RestoreDiagnosticSettingsAsync(CancellationToken token)
    {
        if (dependencies.Preferences is IConsoleDiagnosticPreferences preferences)
            ApplyVerboseLogging(await preferences.LoadVerboseLoggingAsync(token).ConfigureAwait(false));
    }

    public ValueTask SetVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            if (!CanSaveDiagnosticSettings) throw new NotSupportedException("Radio diagnostic settings are unavailable.");
            if (VerboseLoggingEnabled == enabled) return;
            await ((IConsoleDiagnosticPreferences)dependencies.Preferences!).SaveVerboseLoggingAsync(enabled, token).ConfigureAwait(false);
            ApplyVerboseLogging(enabled);
            SetStatus(enabled ? "Verbose radio logging enabled." : "Verbose radio logging disabled.");
        }, cancellationToken);

    private void ApplyVerboseLogging(bool enabled)
    {
        Volatile.Write(ref verboseLogging, enabled);
        var cleanup = new AsyncCleanup();
        foreach (var radio in radios.Sessions.Values.OfType<IRadioDiagnosticSettings>())
            cleanup.Run(() => radio.SetVerboseLogging(enabled));
        cleanup.ThrowIfFailed();
    }
}
