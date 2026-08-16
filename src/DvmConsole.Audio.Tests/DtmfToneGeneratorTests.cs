using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class DtmfToneGeneratorTests
{
    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('*')]
    [InlineData('#')]
    [InlineData('A')]
    [InlineData('d')]
    public void RecognizesStandardDtmfDigits(char digit)
    {
        Assert.True(DtmfToneGenerator.IsDigit(digit));
    }

    [Fact]
    public void GeneratesSequenceWithInterDigitSilence()
    {
        var generator = new DtmfToneGenerator();

        short[] samples = generator.GenerateSequence(
            "1 2",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(20),
            amplitude: 0.25);

        Assert.Equal(1760, samples.Length);
        Assert.Contains(samples, sample => sample != 0);
        Assert.All(samples[800..960], sample => Assert.Equal((short)0, sample));
    }

    [Fact]
    public void RejectsInvalidDigitAndEmptySequence()
    {
        var generator = new DtmfToneGenerator();

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.GenerateDigit('X', TimeSpan.FromMilliseconds(100)));
        Assert.Throws<ArgumentException>(() => generator.GenerateSequence("--", TimeSpan.FromMilliseconds(100), TimeSpan.Zero));
    }

    [Fact]
    public void GeneratesOrderedDigitAndHoldStepsWithoutImplicitGap()
    {
        var generator = new DtmfToneGenerator();

        short[] samples = generator.GenerateSteps(
        [
            new DtmfToneStep('1', TimeSpan.FromMilliseconds(100)),
            new DtmfToneStep('\0', TimeSpan.FromMilliseconds(50), IsHold: true),
            new DtmfToneStep('2', TimeSpan.FromMilliseconds(100))
        ],
        amplitude: 0.25);

        Assert.Equal(2080, samples.Length);
        Assert.All(samples[800..1280], sample => Assert.Equal((short)0, sample));
        Assert.Contains(samples[..800], sample => sample != 0);
        Assert.Contains(samples[1280..], sample => sample != 0);
        Assert.Equal(0, samples.Length % 160);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(50, 60)]
    [InlineData(250, 260)]
    public void AlignsDurationsToTwentyMillisecondVoiceFrames(int milliseconds, int expected)
        => Assert.Equal(
            TimeSpan.FromMilliseconds(expected),
            DtmfToneGenerator.AlignToFrame(TimeSpan.FromMilliseconds(milliseconds)));
}
