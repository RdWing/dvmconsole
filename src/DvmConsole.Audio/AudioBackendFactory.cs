namespace DvmConsole.Audio;

/// <summary>
/// Selects the audio implementation for the current operating system without
/// leaking native backend details into the console application.
/// </summary>
public static class AudioBackendFactory
{
    public static IAudioBackend CreateDefault(string? macLibraryPath = null)
    {
        if (OperatingSystem.IsMacOS())
            return new MacCoreAudioBackend(macLibraryPath);
        if (OperatingSystem.IsWindows())
            return new WindowsAudioBackend();

        throw new PlatformNotSupportedException("No audio backend is available for this operating system.");
    }
}
