// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleMicrophonePresets
{
    private ConsoleMicrophonePresetCatalog microphonePresets = ConsoleMicrophonePresetCatalog.Empty;
    public ConsoleMicrophonePresetCatalog MicrophonePresets => Volatile.Read(ref microphonePresets);
    public bool CanSaveMicrophonePresets => dependencies.Preferences is IConsoleMicrophonePresetStore;

    public ValueTask SaveMicrophonePresetAsync(string name, AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleMicrophonePresetStore
                ?? throw new NotSupportedException("Microphone presets are unavailable.");
            var saved = await store.SaveMicrophonePresetAsync(name, options, token).ConfigureAwait(false);
            Volatile.Write(ref microphonePresets, saved);
        }, cancellationToken);
    }

    public ValueTask DeleteMicrophonePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleMicrophonePresetStore
                ?? throw new NotSupportedException("Microphone presets are unavailable.");
            var saved = await store.DeleteMicrophonePresetAsync(name, token).ConfigureAwait(false);
            Volatile.Write(ref microphonePresets, saved);
        }, cancellationToken);
    }
}
