using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class AudioBackendFactoryTests
{
    [Fact]
    public void HighQualityBluetoothRequiresExplicitOptIn()
    {
        object? factoryDefault = typeof(AudioBackendFactory)
            .GetMethod(nameof(AudioBackendFactory.CreateDefault))!
            .GetParameters()
            .Single(parameter => parameter.Name == "highQualityBluetoothAudio")
            .DefaultValue;
        object? backendDefault = typeof(MacCoreAudioBackend)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == "highQualityBluetoothAudio")
            .DefaultValue;

        Assert.Equal(false, factoryDefault);
        Assert.Equal(false, backendDefault);
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
}
