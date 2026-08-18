using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class LegacyAlertToneGeneratorTests
{
    [Fact]
    public void RecreatesSteadyAlertOne()
    {
        short[] samples = LegacyAlertToneGenerator.Generate(LegacyAlertTone.Alert1);

        Assert.Equal(24_000, samples.Length);
        Assert.InRange(EstimateFrequency(samples), 999.9, 1000.1);
        Assert.InRange(samples.Max(), (short)1840, (short)1850);
        Assert.InRange(samples.Min(), (short)-1850, (short)-1840);
    }

    [Fact]
    public void RecreatesAlternatingAlertTwo()
    {
        short[] samples = LegacyAlertToneGenerator.Generate(LegacyAlertTone.Alert2);

        Assert.Equal(26_880, samples.Length);
        for (int step = 0; step < 14; step++)
        {
            short[] segment = samples[(step * 1920)..((step + 1) * 1920)];
            Assert.Equal(0, segment.Length % 160);
            Assert.Equal((short)0, segment[0]);
            double expected = step % 2 == 0 ? 1500 : 800;
            Assert.InRange(
                EstimateFrequency(segment),
                expected - 0.1,
                expected + 0.1);
        }
    }

    [Fact]
    public void RecreatesEightPulseAlertThree()
    {
        short[] samples = LegacyAlertToneGenerator.Generate(LegacyAlertTone.Alert3);

        Assert.Equal(28_800, samples.Length);
        for (int step = 0; step < 15; step++)
        {
            short[] segment = samples[(step * 1920)..((step + 1) * 1920)];
            Assert.Equal(0, segment.Length % 160);
            if (step % 2 == 0)
                Assert.InRange(EstimateFrequency(segment), 999.9, 1000.1);
            else
                Assert.All(segment, sample => Assert.Equal((short)0, sample));
        }
    }

    [Theory]
    [InlineData(LegacyAlertTone.Alert1)]
    [InlineData(LegacyAlertTone.Alert2)]
    [InlineData(LegacyAlertTone.Alert3)]
    public void DefaultPatternTargetsMinusTwentyFiveDbfs(LegacyAlertTone tone)
    {
        short[] samples = LegacyAlertToneGenerator.Generate(tone);

        double peak = samples.Max(sample => Math.Abs((double)sample)) / short.MaxValue;
        double peakDbfs = 20 * Math.Log10(peak);
        Assert.InRange(peakDbfs, -25.1, -24.9);
    }

    private static double EstimateFrequency(ReadOnlySpan<short> samples)
    {
        List<double> crossings = [];
        for (int index = 1; index < samples.Length; index++)
        {
            short previous = samples[index - 1];
            short current = samples[index];
            if (previous > 0 || current <= 0)
                continue;

            double denominator = Math.Abs((double)previous) + Math.Abs((double)current);
            crossings.Add(index - 1 + (denominator == 0 ? 0 : Math.Abs((double)previous) / denominator));
        }

        Assert.True(crossings.Count > 1);
        return PcmAudioFormat.Voice8KhzMono16Bit.SampleRate * (crossings.Count - 1) /
               (crossings[^1] - crossings[0]);
    }
}
