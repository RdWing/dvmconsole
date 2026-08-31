using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class AudioBackendFactoryTests
{
    [Fact]
    public void DoesNotExposeRemovedHighQualityBluetoothOption()
    {
        Assert.DoesNotContain(
            typeof(AudioBackendFactory)
            .GetMethod(nameof(AudioBackendFactory.CreateDefault))!
            .GetParameters(),
            parameter => parameter.Name == "highQualityBluetoothAudio");
        Assert.DoesNotContain(
            typeof(MacCoreAudioBackend)
            .GetConstructors()
            .Single()
            .GetParameters(),
            parameter => parameter.Name == "highQualityBluetoothAudio");
    }

    [Fact]
    public void RejectsAppleVoiceProcessingOutsideMacOS()
    {
        if (OperatingSystem.IsMacOS())
            return;

        PlatformNotSupportedException exception = Assert.Throws<PlatformNotSupportedException>(() =>
            AudioBackendFactory.CreateDefault(processingMode: AudioProcessingMode.AppleVoiceProcessing));

        Assert.Contains("Apple voice processing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsWindowsCommunicationsProcessingOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        PlatformNotSupportedException exception = Assert.Throws<PlatformNotSupportedException>(() =>
            AudioBackendFactory.CreateDefault(processingMode: AudioProcessingMode.WindowsCommunications));

        Assert.Contains("Windows communications processing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
