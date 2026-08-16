using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class AudioBackendFactoryTests
{
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
