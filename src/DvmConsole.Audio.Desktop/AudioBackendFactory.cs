// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

public sealed class DesktopAudioBackendFactory(
    string? macLibraryPath = null,
    string? linuxLibraryPath = null) :
    IAudioBackendFactory,
    IAudioDeviceChangeSourceFactory
{
    public IAudioBackend Create(AudioBackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AudioBackendFactory.CreateDefault(
            macLibraryPath,
            configuration.ProcessingMode,
            configuration.InputDeviceId,
            configuration.OutputDeviceId,
            linuxLibraryPath);
    }

    public IAudioDeviceChangeSource? CreateDeviceChangeSource()
    {
#if !DVMCONSOLE_MACOS && !DVMCONSOLE_WINDOWS
        if (OperatingSystem.IsLinux())
            return new LinuxPipeWireDeviceChangeSource(linuxLibraryPath);
#endif
        return null;
    }
}

// Selects the audio implementation for the current operating system without
// leaking native backend details into the console application.
public static class AudioBackendFactory
{
    public static IAudioBackend CreateDefault(
        string? macLibraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null,
        string? linuxLibraryPath = null)
    {
#if !DVMCONSOLE_WINDOWS && !DVMCONSOLE_LINUX
        if (OperatingSystem.IsMacOS())
        {
            if (processingMode == AudioProcessingMode.WindowsCommunications)
                throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");
            return new MacCoreAudioBackend(
                macLibraryPath,
                processingMode,
                inputDeviceId,
                outputDeviceId);
        }
#endif
#if !DVMCONSOLE_MACOS && !DVMCONSOLE_LINUX
        if (OperatingSystem.IsWindows())
            return new WindowsAudioBackend(processingMode);
#endif
        if (processingMode == AudioProcessingMode.WindowsCommunications)
            throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");

#if !DVMCONSOLE_MACOS && !DVMCONSOLE_WINDOWS
        if (OperatingSystem.IsLinux())
            return new LinuxPipeWireBackend(linuxLibraryPath);
#endif

        throw new PlatformNotSupportedException("No audio backend is available for this operating system.");
    }
}
