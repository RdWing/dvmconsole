// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleListeningSettings
{
    private bool restoreSelectedChannels = true;
    public bool OutputMuted => state.ReceiveMute.GloballyMuted;
    public bool RestoreSelectedChannelsOnStartup => Volatile.Read(ref restoreSelectedChannels);
    public bool CanSaveStartupPreference => dependencies.Preferences is IConsoleListeningStartupPreferences;

    public ValueTask SetOutputMutedAsync(bool muted, CancellationToken cancellationToken = default)
        => RunCommandAsync(_ =>
        {
            if (OutputMuted == muted) return Task.CompletedTask;
            // This is the same live-PCM discard policy used by Desktop. TAR,
            // decoding and listening intent remain upstream of the speaker mute.
            receive.Audio.SetOutputMuted(muted);
            state.ReceiveMute.SetGlobalMuted(muted);
            SetStatus(muted ? "Live RX output muted. TAR recording continues." : "Live RX output restored.");
            return Task.CompletedTask;
        }, cancellationToken);

    public ValueTask SetRestoreSelectedChannelsAsync(bool restore, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            var preferences = dependencies.Preferences as IConsoleListeningStartupPreferences
                ?? throw new NotSupportedException("Startup preferences are unavailable.");
            if (RestoreSelectedChannelsOnStartup == restore) return;
            await preferences.SaveRestoreSelectedChannelsAsync(restore, token).ConfigureAwait(false);
            Volatile.Write(ref restoreSelectedChannels, restore);
            // Changing next-launch intent must not stop current listening.
            SetStatus(restore ? "Selected channels and web streams will be restored on startup."
                : "Channels and web streams will start unselected. TAR settings are retained.");
        }, cancellationToken);
}
