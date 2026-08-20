using DvmConsole.Audio;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioDeviceSelectorTests
{
    private static readonly IReadOnlyList<AudioDeviceInfo> Outputs =
    [
        new AudioDeviceInfo("built-in", "Built-in output", AudioDirection.Output, true),
        new AudioDeviceInfo("headset", "Headset output", AudioDirection.Output, false)
    ];

    [Fact]
    public void AFixedDeviceDoesNotFollowTheSystemDefault()
    {
        AudioDeviceSelection selection = AudioDeviceSelector.Select(
            Outputs,
            AudioDirection.Output,
            "headset");

        Assert.Equal("headset", selection.Device.Id);
        Assert.False(selection.FollowsSystemDefault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("missing-device")]
    public void DefaultAndFallbackSelectionsFollowTheSystemDefault(string? requestedDeviceId)
    {
        AudioDeviceSelection selection = AudioDeviceSelector.Select(
            Outputs,
            AudioDirection.Output,
            requestedDeviceId);

        Assert.Equal("built-in", selection.Device.Id);
        Assert.True(selection.FollowsSystemDefault);
    }
}
