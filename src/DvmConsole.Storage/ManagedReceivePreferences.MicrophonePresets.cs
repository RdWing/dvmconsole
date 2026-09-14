// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope
    {
        private static ConsoleMicrophonePresetCatalog CaptureMicrophonePresets(UserSettings settings)
            => new(settings.AudioInputPresets.Select(AudioInputPreset.FromSetting)
                .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray(), settings.AudioInputPresetName);

        public ValueTask<ConsoleMicrophonePresetCatalog> LoadMicrophonePresetsAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(CaptureMicrophonePresets, false, cancellationToken);

        public ValueTask<ConsoleMicrophonePresetCatalog> SaveMicrophonePresetAsync(string name, AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return owner.AccessAsync(settings =>
            {
                var preset = AudioInputPreset.Create(name, settings.AudioInputPresets.Count,
                    options.Gain, options.LowGainDb, options.MidGainDb, options.HighGainDb);
                int index = settings.AudioInputPresets.FindIndex(existing => AudioInputPreset.NamesEqual(existing.Name, preset.Name));
                if (index < 0) settings.AudioInputPresets.Add(preset.ToSetting());
                else settings.AudioInputPresets[index] = preset.ToSetting();
                settings.AudioInputPresetName = preset.Name;
                return CaptureMicrophonePresets(settings);
            }, true, cancellationToken);
        }

        public ValueTask<ConsoleMicrophonePresetCatalog> DeleteMicrophonePresetAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return owner.AccessAsync(settings =>
            {
                settings.AudioInputPresets.RemoveAll(preset => AudioInputPreset.NamesEqual(preset.Name, name));
                if (AudioInputPreset.NamesEqual(settings.AudioInputPresetName, name)) settings.AudioInputPresetName = string.Empty;
                return CaptureMicrophonePresets(settings);
            }, true, cancellationToken);
        }
    }
}
