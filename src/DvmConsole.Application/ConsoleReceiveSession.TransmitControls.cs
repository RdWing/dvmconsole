// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession
{
    private ChannelTransmitControlCommands transmitControls => operationalRuntime.TransmitControls;
    private ChannelConfigurationAccess AccessFor(ConsoleChannelState channel)
        => new(channel.Runtime.Definition, dependencies.P25, dependencies.Dmr, dependencies.Nxdn);

    private bool CanSelectTransmit(ConsoleChannelState channel)
        => transmit is not null && transmitChannels.CanTransmitByConfiguration(channel.Id);

    private ValueTask SaveTransmitPreferenceAsync(ChannelId id, ChannelTransmitPreferenceChange change, CancellationToken token)
        => dependencies.Preferences is IConsoleTransmitPreferences preferences
            ? preferences.SaveTransmitAsync(id, change, token) : ValueTask.CompletedTask;

    public ValueTask SetTransmitSelectedAsync(ChannelId id, bool value, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            await transmitControls.SetSelectedAsync(id, value, token).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask SetPageSelectedAsync(ChannelId id, bool value, CancellationToken cancellationToken = default)
        => SetToneSelectionAsync(id, value, paging: true, cancellationToken);

    public ValueTask SetAlertSelectedAsync(ChannelId id, bool value, CancellationToken cancellationToken = default)
        => SetToneSelectionAsync(id, value, paging: false, cancellationToken);

    private ValueTask SetToneSelectionAsync(ChannelId id, bool value, bool paging, CancellationToken cancellationToken)
        => RunCommandAsync(token =>
        {
            transmitControls.SetToneSelected(id, value, paging ? ConsoleToneTargets.Page : ConsoleToneTargets.Alert, token);
            return Task.CompletedTask;
        }, cancellationToken);

    public ValueTask SetTransmitEncryptedAsync(ChannelId id, bool value, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            if (transmit is null) return;
            await manualCommands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (await transmitControls.SetEncryptedAsync(id, value, token).ConfigureAwait(false)) Changed(id);
            }
            finally { manualCommands.Release(); }
        }, cancellationToken);

    private async Task RestoreTransmitPreferencesAsync(CancellationToken token)
    {
        if (transmit is null || dependencies.Preferences is not IConsoleTransmitPreferences preferences) return;
        foreach (var (id, preference) in await preferences.LoadTransmitAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (!state.Channels.TryGetValue(id, out var channel)) continue;
            var access = AccessFor(channel);
            if (preference.Encrypted is { } encrypted && access.CanToggleEncryption(channel.Operator.Snapshot.TransmitEncrypted))
                channel.Operator.SetTransmitEncrypted(encrypted);
            channel.Operator.SetTransmitSelected(preference.Selected && CanSelectTransmit(channel));
            Changed(id);
        }
    }
}
