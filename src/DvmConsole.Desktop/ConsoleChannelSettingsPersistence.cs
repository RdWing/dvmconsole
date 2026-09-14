// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

/// <summary>Persists shared channel preferences without depending on channel views.</summary>
internal sealed class ConsoleChannelSettingsPersistence(
    IReadOnlyList<ConsoleChannelState> channels, UserSettings settings, Action persist)
{
    private readonly IReadOnlyDictionary<ChannelId, ConsoleChannelState> byId = channels.ToDictionary(channel => channel.Id);

    public ValueTask SaveTransmitAsync(ChannelId id, ChannelTransmitPreferenceChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (change.Selected is { } selected)
            settings.TransmitSelectedChannelKeys = channels
                .Where(channel => channel.Id == id ? selected : channel.Operator.Snapshot.TransmitSelected)
                .Select(channel => channel.SettingsKey).ToList();
        if (change.Encrypted is { } encrypted)
            settings.TransmitEncryptionStates[byId[id].SettingsKey] = encrypted;
        persist();
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveRecordingAsync(ChannelId id, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = byId[id].SettingsKey;
        settings.RecordingEnabledChannelKeys.RemoveAll(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (enabled) settings.RecordingEnabledChannelKeys.Add(key);
        persist();
        return ValueTask.CompletedTask;
    }

    public ValueTask SaveAudioAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = byId[id].SettingsKey;
        if (change.Gain is { } gain) settings.ChannelVolumes[key] = gain;
        if (change.Balance is { } balance) settings.ChannelStereoBalances[key] = balance;
        persist();
        return ValueTask.CompletedTask;
    }
}
