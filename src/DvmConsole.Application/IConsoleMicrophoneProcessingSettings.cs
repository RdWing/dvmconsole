// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

/// <summary>Managed microphone DSP choices; input route selection belongs to the host.</summary>
public interface IConsoleMicrophoneProcessingSettings
{
    AudioInputProcessingOptions MicrophoneProcessing { get; }
    bool CanSaveMicrophoneProcessing { get; }
    ValueTask SetMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default);
}

public interface IConsoleMicrophoneProcessingStore
{
    ValueTask<AudioInputProcessingOptions> LoadMicrophoneProcessingAsync(CancellationToken cancellationToken = default);
    ValueTask SaveMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default);
}
