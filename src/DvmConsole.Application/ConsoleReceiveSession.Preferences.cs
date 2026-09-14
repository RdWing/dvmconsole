// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession
{
    private ValueTask SavePreferenceAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken token)
        => dependencies.Preferences?.SaveAsync(id, change, token) ?? ValueTask.CompletedTask;

    private async Task RestorePreferencesAsync(CancellationToken token)
    {
        if (dependencies.Preferences is not { } preferences) return;
        await RestoreDiagnosticSettingsAsync(token).ConfigureAwait(false);
        await RestoreConnectionCuesAsync(token).ConfigureAwait(false);
        await RestoreReceiveBufferingAsync(token).ConfigureAwait(false);
        await RestoreDmrReceiveKeyPolicyAsync(token).ConfigureAwait(false);
        await RestoreRecordingRetentionAsync(token).ConfigureAwait(false);
        await RestoreGroupPreferencesAsync(token).ConfigureAwait(false);
        if (preferences is IConsoleListeningStartupPreferences startup)
            restoreSelectedChannels = await startup.LoadRestoreSelectedChannelsAsync(token).ConfigureAwait(false);
        if (preferences is IConsoleManualTransmitOptionsStore manualOptions)
            manualTransmitOptions = await manualOptions.LoadManualTransmitOptionsAsync(token).ConfigureAwait(false);
        if (preferences is IConsoleMicrophoneProcessingStore microphoneStore)
            microphoneProcessing = NormalizeMicrophoneProcessing(await microphoneStore.LoadMicrophoneProcessingAsync(token).ConfigureAwait(false));
        if (preferences is IConsoleMicrophonePresetStore presetStore)
            microphonePresets = await presetStore.LoadMicrophonePresetsAsync(token).ConfigureAwait(false);
        if (preferences is IConsoleReceiveProcessingStore receiveStore)
            receiveProcessing = await receiveStore.LoadReceiveProcessingAsync(token).ConfigureAwait(false);
        await RestoreTransmitPreferencesAsync(token).ConfigureAwait(false);
        var saved = await preferences.LoadAsync(token).ConfigureAwait(false);
        foreach (var (id, preference) in saved)
        {
            token.ThrowIfCancellationRequested();
            if (!state.Channels.TryGetValue(id, out var channel)) continue;
            channel.RecordingSubscribers.Replace(preference.IgnoredSubscriberIds.IsDefault ? [] : preference.IgnoredSubscriberIds);
            channel.Operator.SetGain(preference.Gain);
            channel.Operator.SetBalance(preference.Balance);
            // Preparation must not compete with the outgoing session for the
            // host's physical output. Playback starts after retirement.
            channel.Operator.SetRecordingEnabled(preference.RecordingEnabled && CanRecord(id));
            channel.Operator.SetAudioEnabled(preference.ReceiveEnabled);
            Changed(id);
        }
        recordingTargets.Refresh();
    }

    /// <summary>Called after the outgoing session releases the host's single output.</summary>
    public Task ActivateListeningAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            try
            {
                // Device-wide preferences may change while a replacement is
                // preparing. Read the latest policy after the outgoing handoff.
                await RestoreReceiveBufferingAsync(token).ConfigureAwait(false);
                await RestoreDmrReceiveKeyPolicyAsync(token).ConfigureAwait(false);
                await RestoreRecordingRetentionAsync(token).ConfigureAwait(false);
                foreach (var (id, channel) in state.Channels)
                {
                    if (channel.Operator.Snapshot.AudioEnabled)
                        await receive.Output.StartAsync(id, false, token).ConfigureAwait(false);
                    else if (channel.Operator.Snapshot.RecordingEnabled)
                        await receive.EnsureRecordingAudioAsync(id, token).ConfigureAwait(false);
                }
                await RefreshLivePatchesAsync(token).ConfigureAwait(false);
                await ResumeWebStreamsAsync(token).ConfigureAwait(false);
                Volatile.Write(ref listeningActivated, 1);
            }
            catch (Exception exception)
            {
                SetStatus($"Restoring listening failed: {exception.Message}");
                throw;
            }
            try
            {
                await PruneRecordingRetentionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Listening has already been restored. Catalog maintenance must
                // not turn a recoverable storage failure into a failed handoff.
                SetStatus($"Listening restored; recording cleanup failed: {exception.Message}");
            }
        }, cancellationToken).AsTask();

}
