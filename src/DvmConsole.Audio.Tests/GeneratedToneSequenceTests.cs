using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class GeneratedToneSequenceTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(2500)]
    public void AcceptsConfiguredSingleToneFrequencyLimits(double frequencyHz)
    {
        GeneratedToneStep step = GeneratedToneStep.Tone(frequencyHz, TimeSpan.FromMilliseconds(20));

        Assert.Equal(frequencyHz, step.FrequencyHz);
    }

    [Theory]
    [InlineData(299.999)]
    [InlineData(2500.001)]
    public void RejectsSingleTonesOutsideConfiguredFrequencyLimits(double frequencyHz)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            GeneratedToneStep.Tone(frequencyHz, TimeSpan.FromMilliseconds(20)));

    [Fact]
    public void PreservesOrderedToneSilenceAndDtmfFrameDurations()
    {
        var sequence = new GeneratedToneSequence(
        [
            GeneratedToneStep.Tone(600, TimeSpan.FromSeconds(1)),
            GeneratedToneStep.Silence(TimeSpan.FromMilliseconds(60)),
            GeneratedToneStep.Tone(1200, TimeSpan.FromSeconds(3)),
            GeneratedToneStep.Dtmf('5', TimeSpan.FromMilliseconds(240))
        ]);

        Assert.Equal([50, 3, 150, 12], sequence.Steps.Select(step => step.FrameCount));
        Assert.Equal(215, sequence.FrameCount);
        Assert.Equal(215 * 160, sequence.RenderPcm().Length);
    }

    [Fact]
    public void QuickCallIiMarksBothPageTonesAsExplicitSingleToneSteps()
    {
        GeneratedToneSequence sequence = QuickCallToneGenerator.CreateSequence(600, 1200);

        Assert.Equal(
            [
                GeneratedToneStepKind.Silence,
                GeneratedToneStepKind.SingleTone,
                GeneratedToneStepKind.SingleTone,
                GeneratedToneStepKind.Silence
            ],
            sequence.Steps.Select(step => step.Kind));
        Assert.Equal([600d, 1200d], sequence.Steps
            .Where(step => step.Kind == GeneratedToneStepKind.SingleTone)
            .Select(step => step.FrequencyHz));
        Assert.Equal([38, 50, 150, 38], sequence.Steps.Select(step => step.FrameCount));
    }
}
