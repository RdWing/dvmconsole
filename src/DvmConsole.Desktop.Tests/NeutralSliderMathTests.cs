using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class NeutralSliderMathTests
{
    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(4, 1)]
    [InlineData(0.5, -0.5)]
    [InlineData(2.5, 0.5)]
    public void VolumePresentationPlacesUnityGainAtCenter(double gain, double position)
    {
        Assert.Equal(position, NeutralSliderMath.VolumeGainToPosition(gain), 6);
        Assert.Equal(gain, NeutralSliderMath.VolumePositionToGain(position), 6);
    }

    [Theory]
    [InlineData(-0.101, -0.101)]
    [InlineData(-0.1, 0)]
    [InlineData(0.099, 0)]
    [InlineData(0.101, 0.101)]
    public void SnapZoneUsesFivePercentOfSliderTravel(double value, double expected)
    {
        Assert.Equal(expected, NeutralSliderMath.SnapToNeutral(value, -1, 1, 0, 0.05), 6);
    }
}
