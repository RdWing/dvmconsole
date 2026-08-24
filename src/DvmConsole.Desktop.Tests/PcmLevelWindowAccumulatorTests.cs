using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PcmLevelWindowAccumulatorTests
{
    [Fact]
    public void P25SizedBatchesProduceAnExactOneSecondWindow()
    {
        var accumulator = new PcmLevelWindowAccumulator(8_000);
        IReadOnlyList<DvmConsole.Audio.PcmLevelMeasurement> measurements = [];

        for (int batch = 0; batch < 6; batch++)
            measurements = accumulator.Observe(new short[1_440]);

        DvmConsole.Audio.PcmLevelMeasurement measurement = Assert.Single(measurements);
        Assert.Equal(8_000, measurement.SampleCount);
        Assert.Equal(640, accumulator.PendingSamples);
    }

    [Fact]
    public void CarriesRemainderIntoTheNextExactWindow()
    {
        var accumulator = new PcmLevelWindowAccumulator(8);

        IReadOnlyList<DvmConsole.Audio.PcmLevelMeasurement> first =
            accumulator.Observe(Enumerable.Repeat((short)1_000, 10).ToArray());
        IReadOnlyList<DvmConsole.Audio.PcmLevelMeasurement> second =
            accumulator.Observe(Enumerable.Repeat((short)2_000, 6).ToArray());

        Assert.Equal(8, Assert.Single(first).SampleCount);
        Assert.Equal(8, Assert.Single(second).SampleCount);
        Assert.Equal(0, accumulator.PendingSamples);
    }
}
