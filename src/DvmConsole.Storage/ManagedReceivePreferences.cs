// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

/// <summary>Uses the existing configuration-ID settings envelope, including its atomic writes and conflict checks.</summary>
public sealed partial class ManagedReceivePreferences(string path) : IConsoleToneSettingsStore, IConsoleToneAssetMaintenance
{
    private readonly string settingsPath = path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public IConsoleReceivePreferences ForConfiguration(ConfigurationId configurationId,
        IReadOnlyDictionary<ChannelId, string> channelKeys)
        => new ConfigurationScope(this, configurationId.ToString(), channelKeys.ToImmutableDictionary());

    private async ValueTask<T> AccessAsync<T>(Func<UserSettings, T> action, bool save, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var store = new UserSettingsStore(settingsPath);
                UserSettings settings = store.Load();
                if (store.LastReadState == SettingsReadState.Unreadable)
                    throw new IOException(store.LastLoadDiagnostics.Warning ?? "Channel preferences are unavailable.");
                T result = action(settings);
                if (save)
                {
                    token.ThrowIfCancellationRequested();
                    store.Save(settings);
                }
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed partial class ConfigurationScope(ManagedReceivePreferences owner, string configurationId,
        ImmutableDictionary<ChannelId, string> keys) : IConsoleReceivePreferences, IConsoleConnectionStartupPreferences, IConsoleListeningStartupPreferences, IConsoleTransmitPreferences, IConsoleGroupPreferences, IConsoleManualTransmitOptionsStore, IConsoleMicrophoneProcessingStore, IConsoleMicrophonePresetStore, IConsoleReceiveProcessingStore, IConsoleDiagnosticPreferences
    {
        public ValueTask<bool> LoadAutoConnectAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => GetState(settings).MobileAutoConnect, false, cancellationToken);

        public async ValueTask SaveAutoConnectAsync(bool enabled, CancellationToken cancellationToken = default)
            => await owner.AccessAsync(settings => GetState(settings).MobileAutoConnect = enabled, true, cancellationToken).ConfigureAwait(false);

        public ValueTask<bool> LoadVerboseLoggingAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => settings.VerboseLoggingEnabled, false, cancellationToken);

        public async ValueTask SaveVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default)
            => await owner.AccessAsync(settings => settings.VerboseLoggingEnabled = enabled, true, cancellationToken).ConfigureAwait(false);

        private ConfigurationOperatorState GetState(UserSettings settings)
        {
            if (!settings.ConfigurationOperatorStates.TryGetValue(configurationId, out var state))
                settings.ConfigurationOperatorStates[configurationId] = state = new();
            return state;
        }

        public ValueTask<ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken cancellationToken)
            => owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                return keys.ToImmutableDictionary(pair => pair.Key, pair => new ChannelReceivePreferences(
                    state.ChannelVolumes.GetValueOrDefault(pair.Value, 1),
                    state.ChannelStereoBalances.GetValueOrDefault(pair.Value),
                    state.RestoreSelectedChannelsOnStartup && state.ReceiveEnabledChannelKeys.Contains(pair.Value, StringComparer.OrdinalIgnoreCase),
                    state.RecordingEnabledChannelKeys.Contains(pair.Value, StringComparer.OrdinalIgnoreCase),
                    state.RecordingIgnoredSubscriberIds.GetValueOrDefault(pair.Value, []).ToImmutableArray()));
            }, false, cancellationToken);

        public async ValueTask SaveAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken cancellationToken)
        {
            string key = keys[id];
            await owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                if (change.Gain is { } gain) state.ChannelVolumes[key] = gain;
                if (change.Balance is { } balance) state.ChannelStereoBalances[key] = balance;
                SetSelection(state.ReceiveEnabledChannelKeys, key, change.ReceiveEnabled);
                SetSelection(state.RecordingEnabledChannelKeys, key, change.RecordingEnabled);
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<ImmutableDictionary<ChannelId, ChannelTransmitPreferences>> LoadTransmitAsync(CancellationToken cancellationToken)
            => owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                return keys.ToImmutableDictionary(pair => pair.Key, pair => new ChannelTransmitPreferences(
                    state.RestoreSelectedChannelsOnStartup && state.TransmitSelectedChannelKeys.Contains(pair.Value, StringComparer.OrdinalIgnoreCase),
                    state.TransmitEncryptionStates.TryGetValue(pair.Value, out bool encrypted) ? encrypted : null));
            }, false, cancellationToken);

        public async ValueTask SaveTransmitAsync(ChannelId id, ChannelTransmitPreferenceChange change, CancellationToken cancellationToken)
        {
            string key = keys[id];
            await owner.AccessAsync(settings =>
            {
                var state = GetState(settings);
                SetSelection(state.TransmitSelectedChannelKeys, key, change.Selected);
                if (change.Encrypted is { } encrypted) state.TransmitEncryptionStates[key] = encrypted;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<bool> LoadRestoreSelectedChannelsAsync(CancellationToken cancellationToken)
            => owner.AccessAsync(settings => GetState(settings).RestoreSelectedChannelsOnStartup, false, cancellationToken);

        public async ValueTask SaveRestoreSelectedChannelsAsync(bool restore, CancellationToken cancellationToken)
            => await owner.AccessAsync(settings =>
            {
                GetState(settings).RestoreSelectedChannelsOnStartup = restore;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);

        private static void SetSelection(List<string> selection, string key, bool? enabled)
        {
            if (enabled is null) return;
            selection.RemoveAll(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
            if (enabled.Value) selection.Add(key);
        }
    }
}
