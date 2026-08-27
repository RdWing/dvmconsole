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

    [Theory]
    [InlineData(4_500)]
    [InlineData(7_000)]
    public void DownsamplingSuppressesFrequenciesAboveDestinationNyquist(int frequency)
    {
        const int inputRate = 48_000;
        const int outputRate = 8_000;
        const int amplitude = 12_000;
        var converter = new PcmRateConverter(inputRate, outputRate);
        short[] input = CreateTone(inputRate, frequency, amplitude, seconds: 1);

        short[] output = converter.Convert(input);

        Assert.Equal(outputRate, output.Length);
        double rms = CalculateRms(output.AsSpan(outputRate / 10));
        Assert.True(rms < 30, $"Expected stop-band RMS below 30, but measured {rms:0.###}.");
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(3_000)]
    [InlineData(3_300)]
    public void DownsamplingPreservesSpeechBandAmplitude(int frequency)
    {
        const int inputRate = 48_000;
        const int outputRate = 8_000;
        const int amplitude = 12_000;
        var converter = new PcmRateConverter(inputRate, outputRate);
        short[] input = CreateTone(inputRate, frequency, amplitude, seconds: 1);

        short[] output = converter.Convert(input);

        double expectedRms = amplitude / Math.Sqrt(2);
        double actualRms = CalculateRms(output.AsSpan(outputRate / 10));
        double retainedAmplitude = actualRms / expectedRms;
        Assert.True(
            retainedAmplitude is >= 0.98 and <= 1.01,
            $"Expected speech-band amplitude within 2%, but retained {retainedAmplitude:P2}.");
    }

    [Fact]
    public void DownsamplingIsIndependentOfInputChunkBoundaries()
    {
        short[] input = CreateTone(48_000, frequency: 1_137, amplitude: 10_000, seconds: 1);
        var wholeInputConverter = new PcmRateConverter(48_000, 8_000);
        var chunkedConverter = new PcmRateConverter(48_000, 8_000);

        short[] expected = wholeInputConverter.Convert(input);
        var actual = new List<short>();
        int offset = 0;
        int[] chunkSizes = [37, 480, 113, 997, 64];
        for (int chunkIndex = 0; offset < input.Length; chunkIndex++)
        {
            int count = Math.Min(chunkSizes[chunkIndex % chunkSizes.Length], input.Length - offset);
            actual.AddRange(chunkedConverter.Convert(input.AsSpan(offset, count)));
            offset += count;
        }

        Assert.Equal(expected, actual);
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

    [Fact]
    public void StereoDownsamplingPreservesInterleavedChannelSeparation()
    {
        var converter = new PcmRateConverter(48_000, 8_000, channels: 2);
        short[] input = Enumerable.Range(0, 480)
            .SelectMany(_ => new short[] { 400, -700 })
            .ToArray();

        short[] output = converter.Convert(input);

        Assert.Equal(160, output.Length);
        for (int index = 0; index < output.Length; index += 2)
        {
            Assert.Equal((short)400, output[index]);
            Assert.Equal((short)-700, output[index + 1]);
        }
    }

    [Theory]
    [InlineData(8_000, 48_000)]
    [InlineData(48_000, 8_000)]
    public void CallerOwnedBufferConversionMatchesAllocatingApiAcrossChunks(
        int inputRate,
        int outputRate)
    {
        var allocating = new PcmRateConverter(inputRate, outputRate);
        var buffered = new PcmRateConverter(inputRate, outputRate);
        short[][] chunks = [[0, 100], [200], [300, 400, 500]];

        foreach (short[] chunk in chunks)
        {
            short[] expected = allocating.Convert(chunk);
            int maximum = buffered.GetMaximumOutputSampleCount(chunk.Length);
            var destination = new short[maximum];

            int count = buffered.Convert(chunk, destination);

            Assert.Equal(expected, destination.AsSpan(0, count).ToArray());
        }
    }

    [Fact]
    public void CallerOwnedBufferRequiresCompleteFramesAndEnoughCapacity()
    {
        var stereo = new PcmRateConverter(8_000, 48_000, channels: 2);

        Assert.Throws<ArgumentException>(() => stereo.GetMaximumOutputSampleCount(1));
        Assert.Throws<ArgumentException>(() => stereo.Convert([0, 1, 2], new short[32]));
        Assert.Throws<ArgumentException>(() => stereo.Convert([0, 1, 2, 3], Span<short>.Empty));
    }

    private static short[] CreateTone(int sampleRate, int frequency, int amplitude, int seconds)
        => Enumerable.Range(0, checked(sampleRate * seconds))
            .Select(index => (short)Math.Round(
                amplitude * Math.Sin(2 * Math.PI * frequency * index / sampleRate)))
            .ToArray();

    private static double CalculateRms(ReadOnlySpan<short> samples)
    {
        double sumSquares = 0;
        foreach (short sample in samples)
            sumSquares += sample * (double)sample;
        return Math.Sqrt(sumSquares / samples.Length);
    }
}
