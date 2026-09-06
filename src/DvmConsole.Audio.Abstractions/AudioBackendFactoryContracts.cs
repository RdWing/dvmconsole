// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

public sealed record AudioBackendConfiguration(
    AudioProcessingMode ProcessingMode,
    string InputDeviceId,
    string OutputDeviceId)
{
    public static AudioBackendConfiguration Default { get; } = new(
        AudioProcessingMode.DvmConsole,
        "default",
        "default");
}

public interface IAudioBackendFactory
{
    IAudioBackend Create(AudioBackendConfiguration configuration);
}

// Optional composition capability for hosts with event-driven audio topology
// notifications. Consumers remain portable by retaining their polling path.
public interface IAudioDeviceChangeSourceFactory
{
    IAudioDeviceChangeSource? CreateDeviceChangeSource();
}
