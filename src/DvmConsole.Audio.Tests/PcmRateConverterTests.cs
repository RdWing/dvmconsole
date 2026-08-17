using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmRateConverterTests
{
    [Fact]
    public void SameRatePreservesSamplesExactly()
    {
        var converter = new PcmRateConverter(8_000, 8_000);

        Assert.Equal(new short[] { -4, 0, 7, 123 }, converter.Convert(new short[] { -4, 0, 7, 123 }));
    }

    [Fact]
    public void DownsamplingMaintainsOutputRateAcrossChunks()
    {
        var converter = new PcmRateConverter(48_000, 8_000);
        short[] input = Enumerable.Repeat((short)321, 480).ToArray();

        short[] first = converter.Convert(input);
        short[] second = converter.Convert(input);

        Assert.Equal(80, first.Length);
        Assert.Equal(80, second.Length);
        Assert.All(first, sample => Assert.Equal((short)321, sample));
        Assert.All(second, sample => Assert.Equal((short)321, sample));
    }

    [Fact]
    public void UpsamplingInterpolatesAndKeepsChunkBoundaryState()
    {
        var converter = new PcmRateConverter(8_000, 48_000);

        short[] first = converter.Convert(new short[] { 0, 100 });
        short[] second = converter.Convert(new short[] { 200 });

        Assert.Equal(new short[] { 0, 17, 33, 50, 67, 83 }, first);
        Assert.Equal(new short[] { 100, 117, 133, 150, 167, 183 }, second);
    }

    [Fact]
    public void StereoConversionPreservesInterleavedChannelSeparation()
    {
        var converter = Assert.IsType<PcmRateConverter>(Activator.CreateInstance(
            typeof(PcmRateConverter),
            8_000,
            16_000,
            2));

        short[] output = converter.Convert(new short[] { 0, 1_000, 100, 1_100 });

        Assert.Equal(new short[] { 0, 1_000, 50, 1_050 }, output);
    }
}
