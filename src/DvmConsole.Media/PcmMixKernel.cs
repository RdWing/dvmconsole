namespace DvmConsole.Media;

internal static class PcmMixKernel
{
    public static void Clear(double[] leftMix, double[]? rightMix)
    {
        Array.Clear(leftMix);
        if (rightMix is not null)
            Array.Clear(rightMix);
    }

    public static int Accumulate(
        ReadOnlySpan<short> source,
        double gain,
        double balance,
        double[] leftMix,
        double[]? rightMix)
    {
        int count = Math.Min(leftMix.Length, source.Length);
        double leftBalance = rightMix is null || balance <= 0 ? 1.0 : 1.0 - balance;
        double rightBalance = balance >= 0 ? 1.0 : 1.0 + balance;
        for (int index = 0; index < count; index++)
        {
            double gained = source[index] * gain;
            leftMix[index] += gained * leftBalance;
            if (rightMix is not null)
                rightMix[index] += gained * rightBalance;
        }

        return count;
    }

    public static bool Render(
        double[] leftMix,
        double[]? rightMix,
        int outputChannels,
        short[] output)
    {
        double peak = 0;
        for (int index = 0; index < leftMix.Length; index++)
        {
            peak = Math.Max(peak, Math.Abs(leftMix[index]));
            if (rightMix is not null)
                peak = Math.Max(peak, Math.Abs(rightMix[index]));
        }

        double protection = peak > short.MaxValue ? short.MaxValue / peak : 1.0;
        for (int index = 0; index < leftMix.Length; index++)
        {
            int outputIndex = index * outputChannels;
            output[outputIndex] = ToPcm(leftMix[index] * protection);
            if (rightMix is not null)
                output[outputIndex + 1] = ToPcm(rightMix[index] * protection);
        }

        return protection < 1.0;
    }

    public static short ToPcm(double sample)
        => (short)Math.Clamp(
            Math.Round(sample, MidpointRounding.AwayFromZero),
            short.MinValue,
            short.MaxValue);
}
