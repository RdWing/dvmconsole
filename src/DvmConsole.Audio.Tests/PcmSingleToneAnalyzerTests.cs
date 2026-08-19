using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmSingleToneAnalyzerTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(2500)]
    public void DetectsSustainedSingleTones(double frequencyHz)
    {
        short[] samples = new PcmToneGenerator().GenerateTone(
            frequencyHz,
            TimeSpan.FromMilliseconds(240),
            amplitude: 0.35);

        double?[] detected = PcmSingleToneAnalyzer.Analyze(samples);

        Assert.True(detected.Count(frequency => frequency.HasValue) >= 10);
        Assert.All(
            detected.Where(frequency => frequency.HasValue),
            frequency => Assert.InRange(frequency!.Value, frequencyHz - 20, frequencyHz + 20));
    }

    [Fact]
    public void RejectsDtmfAsASingleTone()
    {
        short[] samples = new DtmfToneGenerator().GenerateDigit(
            '5',
            TimeSpan.FromMilliseconds(240),
            amplitude: 0.35);

        double?[] detected = PcmSingleToneAnalyzer.Analyze(samples);

        Assert.All(detected, frequency => Assert.Null(frequency));
    }

    [Fact]
    public void KeepsUncertainAudioOnVoicePathBetweenToneRegions()
    {
        var toneGenerator = new PcmToneGenerator();
        short[] firstTone = toneGenerator.GenerateTone(800, TimeSpan.FromMilliseconds(200), 0.35);
        short[] dtmf = new DtmfToneGenerator().GenerateDigit('8', TimeSpan.FromMilliseconds(200), 0.35);
        short[] secondTone = toneGenerator.GenerateTone(1500, TimeSpan.FromMilliseconds(200), 0.35);
        short[] samples = [.. firstTone, .. dtmf, .. secondTone];

        double?[] detected = PcmSingleToneAnalyzer.Analyze(samples);

        Assert.Contains(detected[..8], frequency => frequency is >= 780 and <= 820);
        Assert.All(detected[12..18], frequency => Assert.Null(frequency));
        Assert.Contains(detected[22..], frequency => frequency is >= 1480 and <= 1520);
    }
}
