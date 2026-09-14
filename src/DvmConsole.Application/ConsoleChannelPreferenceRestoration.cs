// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>Restores saved operator intent before media services or views consume channel state.</summary>
internal static class ConsoleChannelPreferenceRestoration
{
    public static void Apply(IEnumerable<ConsoleChannelState> channels, UserSettings settings)
    {
        var recording = settings.RecordingEnabledChannelKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var receive = settings.ReceiveEnabledChannelKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transmit = settings.TransmitSelectedChannelKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ConsoleChannelState channel in channels)
        {
            string key = channel.SettingsKey;
            var definition = channel.Runtime.Definition;
            ChannelOperatorState operation = channel.Operator;
            if (definition.IsEncrypted && definition.SelectableEncryption &&
                settings.TransmitEncryptionStates.TryGetValue(key, out bool encrypted))
                operation.SetTransmitEncrypted(encrypted);

            operation.SetGain(settings.ChannelVolumes.GetValueOrDefault(key, 1.0));
            operation.SetBalance(settings.ChannelStereoBalances.GetValueOrDefault(key, 0.0));
            operation.SetOutputRoute(settings.ChannelOutputDeviceIds.GetValueOrDefault(key)?.Trim());
            operation.SetRecordingEnabled(recording.Contains(key));
            channel.RecordingSubscribers.Replace(settings.RecordingIgnoredSubscriberIds.TryGetValue(key, out var ignored)
                ? ignored : []);
            if (settings.RestoreSelectedChannelsOnStartup && receive.Contains(key))
                channel.SetReceiveEnabled(true);
            operation.SetTransmitSelected(transmit.Contains(key));
        }
    }
}
