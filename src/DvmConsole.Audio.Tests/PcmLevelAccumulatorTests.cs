using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmLevelAccumulatorTests
{
    [Fact]
    public void AggregatesEverySampleAcrossInputChunks()
    {
        var accumulator = new PcmLevelAccumulator();

        accumulator.Add(Enumerable.Repeat((short)short.MaxValue, 160).ToArray());
        accumulator.Add(new short[160]);

        Assert.True(accumulator.TryMeasureAndReset(out PcmLevelMeasurement measurement));
        Assert.Equal(320, measurement.SampleCount);
        Assert.InRange(measurement.RmsDbfs, -3.02, -3.00);
        Assert.InRange(measurement.PeakDbfs, -0.001, 0);
    }

    [Fact]
    public void HandlesFullScaleNegativePcmWithoutOverflow()
    {
        var accumulator = new PcmLevelAccumulator();

        accumulator.Add([short.MinValue]);

        Assert.True(accumulator.TryMeasureAndReset(out PcmLevelMeasurement measurement));
        Assert.Equal(0, measurement.RmsDbfs);
        Assert.Equal(0, measurement.PeakDbfs);
    }

    [Fact]
    public void MeasurementResetsTheWindow()
    {
        var accumulator = new PcmLevelAccumulator();
        accumulator.Add([16_384]);

        Assert.True(accumulator.TryMeasureAndReset(out PcmLevelMeasurement measurement));
        Assert.InRange(measurement.RmsDbfs, -6.03, -6.01);
        Assert.Equal(0, accumulator.SampleCount);
        Assert.False(accumulator.TryMeasureAndReset(out _));
    }

    [Fact]
    public void ExplicitResetDiscardsAnIncompleteWindow()
    {
        var accumulator = new PcmLevelAccumulator();
        accumulator.Add([1_000, -1_000]);

        accumulator.Reset();

        Assert.Equal(0, accumulator.SampleCount);
        Assert.False(accumulator.TryMeasureAndReset(out _));
    }
}
