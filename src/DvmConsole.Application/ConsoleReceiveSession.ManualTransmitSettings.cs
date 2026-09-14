// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleManualTransmitSettings
{
    private ConsoleManualTransmitOptions manualTransmitOptions = new();
    private ConsoleManualTransmitOptions activeManualTransmitOptions = new();

    public ConsoleManualTransmitOptions ManualTransmitOptions => Volatile.Read(ref manualTransmitOptions);
    public bool CanSaveManualTransmitOptions => dependencies.Preferences is IConsoleManualTransmitOptionsStore;

    public ValueTask SetManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleManualTransmitOptionsStore
                ?? throw new NotSupportedException("Manual transmit preferences are unavailable.");
            if (ManualTransmitOptions == options) return;
            await store.SaveManualTransmitOptionsAsync(options, token).ConfigureAwait(false);
            Volatile.Write(ref manualTransmitOptions, options);
            SetStatus("Audio settings saved for the next manual transmission.");
        }, cancellationToken);
    }
}
