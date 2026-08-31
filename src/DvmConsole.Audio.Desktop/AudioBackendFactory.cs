namespace DvmConsole.Audio;

public sealed class DesktopAudioBackendFactory(string? macLibraryPath = null) : IAudioBackendFactory
{
    public IAudioBackend Create(AudioBackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AudioBackendFactory.CreateDefault(
            macLibraryPath,
            configuration.ProcessingMode,
            configuration.InputDeviceId,
            configuration.OutputDeviceId);
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
        string? outputDeviceId = null)
    {
#if !DVMCONSOLE_WINDOWS
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
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
            throw new PlatformNotSupportedException("Apple voice processing requires an Apple audio backend.");
#if !DVMCONSOLE_MACOS
        if (OperatingSystem.IsWindows())
            return new WindowsAudioBackend(processingMode);
#endif
        if (processingMode == AudioProcessingMode.WindowsCommunications)
            throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");

        throw new PlatformNotSupportedException("No audio backend is available for this operating system.");
    }
}
