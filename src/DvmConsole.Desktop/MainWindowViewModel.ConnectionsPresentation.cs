// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;
using DvmConsole.Media;
using DvmConsole.Threading;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IConnectionsSettingsViewModel
{
    System.Collections.IEnumerable IConnectionsSettingsViewModel.ConnectionSystems => Systems;
    System.Collections.IEnumerable IConnectionsSettingsViewModel.KeyStatusItems => KeyStatusItems;

    public bool RequireConfiguredDmrReceiveKey
    {
        get => userSettings.RequireConfiguredDmrReceiveKey;
        set
        {
            if (userSettings.RequireConfiguredDmrReceiveKey == value)
                return;

            userSettings.RequireConfiguredDmrReceiveKey = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RequireConfiguredDmrReceiveKey)));
            TaskObservation.Observe(
                ApplyDmrReceiveKeyPolicyAsync(value),
                HandleAudioCommandFault);
        }
    }

    private DmrReceiveKeyPolicy GetDmrReceiveKeyPolicy()
        => userSettings.RequireConfiguredDmrReceiveKey
            ? DmrReceiveKeyPolicy.ConfiguredChannel
            : DmrReceiveKeyPolicy.OnAirMetadata;

    private async Task ApplyDmrReceiveKeyPolicyAsync(bool requireConfiguredKey)
    {
        await RestartReceiveVocoderSessionsAsync().ConfigureAwait(false);
        if (userSettings.RequireConfiguredDmrReceiveKey != requireConfiguredKey)
            return;

        await RunOnUiThreadAsync(() =>
            AudioStatusText = requireConfiguredKey
                ? "DMR receive now requires the channel's configured algorithm and key ID."
                : "DMR receive now follows on-air key identifiers within each FNE system.")
            .ConfigureAwait(false);
    }
}
