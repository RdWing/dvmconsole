namespace DvmConsole.Audio;

// Selects the audio implementation for the current operating system without
// leaking native backend details into the console application.
public static class AudioBackendFactory
{
    public static IAudioBackend CreateDefault(
        string? macLibraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null,
        bool highQualityBluetoothAudio = false)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (processingMode == AudioProcessingMode.WindowsCommunications)
                throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");
            return new MacCoreAudioBackend(
                macLibraryPath,
                processingMode,
                inputDeviceId,
                outputDeviceId,
                highQualityBluetoothAudio);
        }
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
            throw new PlatformNotSupportedException("Apple voice processing requires an Apple audio backend.");
        if (OperatingSystem.IsWindows())
            return new WindowsAudioBackend(processingMode);
        if (processingMode == AudioProcessingMode.WindowsCommunications)
            throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");

        throw new PlatformNotSupportedException("No audio backend is available for this operating system.");
    }
}
