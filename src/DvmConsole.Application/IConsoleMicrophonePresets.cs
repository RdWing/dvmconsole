// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

public sealed record ConsoleMicrophonePresetCatalog(ImmutableArray<AudioInputPreset> Presets, string SelectedName)
{
    public static ConsoleMicrophonePresetCatalog Empty { get; } = new([], string.Empty);
}

public interface IConsoleMicrophonePresets
{
    ConsoleMicrophonePresetCatalog MicrophonePresets { get; }
    bool CanSaveMicrophonePresets { get; }
    ValueTask SaveMicrophonePresetAsync(string name, AudioInputProcessingOptions options, CancellationToken cancellationToken = default);
    ValueTask DeleteMicrophonePresetAsync(string name, CancellationToken cancellationToken = default);
}

public interface IConsoleMicrophonePresetStore
{
    ValueTask<ConsoleMicrophonePresetCatalog> LoadMicrophonePresetsAsync(CancellationToken cancellationToken = default);
    ValueTask<ConsoleMicrophonePresetCatalog> SaveMicrophonePresetAsync(string name, AudioInputProcessingOptions options, CancellationToken cancellationToken = default);
    ValueTask<ConsoleMicrophonePresetCatalog> DeleteMicrophonePresetAsync(string name, CancellationToken cancellationToken = default);
}
