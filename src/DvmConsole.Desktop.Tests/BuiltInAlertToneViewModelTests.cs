using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BuiltInAlertToneViewModelTests
{
    [Theory]
    [InlineData(LegacyAlertTone.Alert1, 24_000)]
    [InlineData(LegacyAlertTone.Alert3, 28_800)]
    public void GenerateSamplesUsesCalibratedVocoderAlignedWaveform(
        LegacyAlertTone tone,
        int expectedSampleCount)
    {
        var viewModel = new BuiltInAlertToneViewModel(tone);

        short[] samples = viewModel.GenerateSamples();

        Assert.Equal(expectedSampleCount, samples.Length);
        Assert.Equal(0, samples.Length % 160);
        double peak = samples.Max(sample => Math.Abs((double)sample)) / short.MaxValue;
        double peakDbfs = 20 * Math.Log10(peak);
        Assert.InRange(peakDbfs, -25.1, -24.9);
    }
}
