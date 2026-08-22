using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class PcmMixKernelTests
{
    [Fact]
    public void MixesMonoSampleForSample()
    {
        double[] left = new double[3];
        short[] output = new short[3];

        PcmMixKernel.Accumulate(new short[] { 1_000, -2_000, 3_000 }, 1.5, 0, left, null);
        PcmMixKernel.Accumulate(new short[] { 500, 500, -500 }, 0.5, 0, left, null);
        bool protectedFrame = PcmMixKernel.Render(left, null, 1, output);

        Assert.False(protectedFrame);
        Assert.Equal(new short[] { 1_750, -2_750, 4_250 }, output);
    }

    [Fact]
    public void AppliesStereoBalanceWithoutChangingRounding()
    {
        double[] left = new double[2];
        double[] right = new double[2];
        short[] output = new short[4];

        PcmMixKernel.Accumulate(new short[] { 1_001, -1_001 }, 0.5, 1, left, right);
        PcmMixKernel.Render(left, right, 2, output);

        Assert.Equal(new short[] { 0, 501, 0, -501 }, output);
    }

    [Fact]
    public void ProtectsClippingFrameWithSharedScale()
    {
        double[] left = { 60_000, -30_000 };
        short[] output = new short[2];

        bool protectedFrame = PcmMixKernel.Render(left, null, 1, output);

        Assert.True(protectedFrame);
        Assert.Equal(short.MaxValue, output[0]);
        Assert.Equal((short)-16_384, output[1]);
    }
}
