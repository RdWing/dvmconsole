using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmToneGeneratorTests
{
    [Fact]
    public void GeneratesEightKhzMonoToneWithBoundedAmplitude()
    {
        var generator = new PcmToneGenerator();

        short[] samples = generator.GenerateTone(1000, TimeSpan.FromMilliseconds(100), 0.25);

        Assert.Equal(800, samples.Length);
        Assert.Equal((short)0, samples[0]);
        Assert.Contains(samples, sample => sample != 0);
        Assert.InRange(samples.Max(), 8000, 8200);
        Assert.InRange(samples.Min(), -8200, -8000);
    }

    [Fact]
    public void GeneratesDualToneAndRejectsInvalidFrequency()
    {
        var generator = new PcmToneGenerator();

        short[] samples = generator.GenerateDualTone(697, 1209, TimeSpan.FromMilliseconds(250));

        Assert.Equal(2000, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.GenerateTone(4000, TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.GenerateTone(1000, TimeSpan.Zero));
    }

    [Fact]
    public void GeneratesOrderedToneAndHoldSteps()
    {
        var generator = new PcmToneGenerator();

        short[] samples = generator.GenerateSteps(
        [
            new PcmToneStep(1000, TimeSpan.FromMilliseconds(100)),
            new PcmToneStep(0, TimeSpan.FromMilliseconds(50), IsHold: true),
            new PcmToneStep(1200, TimeSpan.FromMilliseconds(100))
        ],
        amplitude: 0.25);

        Assert.Equal(2000, samples.Length);
        Assert.All(samples[800..1200], sample => Assert.Equal((short)0, sample));
        Assert.Contains(samples[..800], sample => sample != 0);
        Assert.Contains(samples[1200..], sample => sample != 0);
    }

    [Fact]
    public void GeneratesLegacyQuickCallIiTiming()
    {
        short[] samples = QuickCallToneGenerator.Generate(600, 1200);

        Assert.Equal(44_160, samples.Length);
        Assert.All(samples[..6_080], sample => Assert.Equal((short)0, sample));
        Assert.Contains(samples[6_080..14_080], sample => sample != 0);
        Assert.Contains(samples[14_080..38_080], sample => sample != 0);
        Assert.All(samples[38_080..], sample => Assert.Equal((short)0, sample));
    }

    [Theory]
    [InlineData("600", "1200", true)]
    [InlineData("0", "1200", false)]
    [InlineData("600", "2501", false)]
    [InlineData("299", "1200", false)]
    [InlineData("", "1200", false)]
    public void ValidatesQuickCallIiFrequencyInputs(
        string toneAText,
        string toneBText,
        bool expected)
    {
        bool result = QuickCallToneGenerator.TryParse(
            toneAText,
            toneBText,
            out _,
            out _,
            out _);

        Assert.Equal(expected, result);
    }
}
