using System.Buffers;

namespace DvmConsole.Audio;

// Small streaming PCM rate converter used when a device or decoded media
// exposes a rate different from the 8 kHz voice rate. Downsampling applies a
// low-pass filter before interpolation so frequencies above the destination
// Nyquist limit do not fold into the radio voice band.
public sealed class PcmRateConverter
{
    private const int MinimumDownsamplingFilterTaps = 63;
    private const int MaximumDownsamplingFilterTaps = 511;
    // Preserve roughly the same transition width as the common 48 kHz to
    // 8 kHz path, which uses 255 taps and adds about 2.7 ms of group delay.
    private const double DownsamplingFilterTapsPerRateRatio = 42.5;
    // Place the 8 kHz cutoff at 3.7 kHz, leaving a transition band before the
    // 4 kHz destination Nyquist limit without trimming ordinary voice audio.
    private const double DownsamplingCutoffFraction = 0.4625;
    // Kaiser beta for approximately 60 dB of stop-band rejection.
    private const double KaiserBeta = 5.65326;

    private readonly int inputRate;
    private readonly int outputRate;
    private readonly int channels;
    private readonly double step;
    private readonly List<short> pending = [];
    private readonly double[]? downsamplingCoefficients;
    private readonly double[]? downsamplingHistory;
    private int downsamplingHistoryFrameIndex;
    private bool downsamplingHistoryInitialized;
    private double sourcePosition;

    public PcmRateConverter(int inputRate, int outputRate, int channels = 1)
    {
        if (inputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        this.inputRate = inputRate;
        this.outputRate = outputRate;
        this.channels = channels;
        step = (double)inputRate / outputRate;
        if (outputRate < inputRate)
        {
            int tapCount = CalculateDownsamplingFilterTapCount(inputRate, outputRate);
            downsamplingCoefficients = CreateDownsamplingFilter(inputRate, outputRate, tapCount);
            downsamplingHistory = new double[checked(tapCount * channels)];
        }
    }

    public short[] Convert(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return [];
        if (samples.Length % channels != 0)
            throw new ArgumentException("Interleaved PCM must contain complete channel frames.", nameof(samples));
        if (inputRate == outputRate)
            return samples.ToArray();

        int maximumOutputSamples = GetMaximumOutputSampleCount(samples.Length);
        short[] output = ArrayPool<short>.Shared.Rent(maximumOutputSamples);
        try
        {
            int outputSamples = Convert(samples, output);
            return output.AsSpan(0, outputSamples).ToArray();
        }
        finally
        {
            ArrayPool<short>.Shared.Return(output);
        }
    }

    public int GetMaximumOutputSampleCount(int inputSampleCount)
    {
        if (inputSampleCount < 0 || inputSampleCount % channels != 0)
        {
            throw new ArgumentException(
                "Interleaved PCM must contain complete channel frames.",
                nameof(inputSampleCount));
        }
        if (inputRate == outputRate)
            return inputSampleCount;

        int totalFrames = checked((pending.Count + inputSampleCount) / channels);
        if (totalFrames < 2 || sourcePosition + 1 >= totalFrames)
            return 0;

        double sourceFramesAvailable = totalFrames - 1 - sourcePosition;
        // Floating-point accumulation can leave sourcePosition infinitesimally
        // below an exact frame boundary. Reserve one additional frame so the
        // caller-owned buffer also covers that valid interpolation step.
        int outputFrames = checked((int)Math.Ceiling(sourceFramesAvailable / step) + 1);
        return checked(outputFrames * channels);
    }

    public int Convert(ReadOnlySpan<short> samples, Span<short> destination)
    {
        if (samples.Length % channels != 0)
            throw new ArgumentException("Interleaved PCM must contain complete channel frames.", nameof(samples));
        if (inputRate == outputRate)
        {
            if (destination.Length < samples.Length)
                throw new ArgumentException("The output buffer is too small.", nameof(destination));
            samples.CopyTo(destination);
            return samples.Length;
        }

        int maximumOutputSamples = GetMaximumOutputSampleCount(samples.Length);
        if (destination.Length < maximumOutputSamples)
            throw new ArgumentException("The output buffer is too small.", nameof(destination));

        AppendInput(samples);

        int outputIndex = 0;
        while (sourcePosition + 1 < pending.Count / channels)
        {
            int frameIndex = (int)sourcePosition;
            double fraction = sourcePosition - frameIndex;
            for (int channel = 0; channel < channels; channel++)
            {
                int index = (frameIndex * channels) + channel;
                int nextIndex = index + channels;
                double value = pending[index] + ((pending[nextIndex] - pending[index]) * fraction);
                destination[outputIndex++] =
                    (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            }
            sourcePosition += step;
        }

        int removableFrames = Math.Min(
            (int)sourcePosition,
            Math.Max(0, (pending.Count / channels) - 1));
        if (removableFrames > 0)
        {
            pending.RemoveRange(0, removableFrames * channels);
            sourcePosition -= removableFrames;
        }

        return outputIndex;
    }

    private void AppendInput(ReadOnlySpan<short> samples)
    {
        pending.EnsureCapacity(checked(pending.Count + samples.Length));
        if (downsamplingCoefficients is null || downsamplingHistory is null)
        {
            for (int index = 0; index < samples.Length; index++)
                pending.Add(samples[index]);
            return;
        }

        if (!downsamplingHistoryInitialized && !samples.IsEmpty)
            InitializeDownsamplingHistory(samples, downsamplingHistory, downsamplingCoefficients.Length);

        int tapCount = downsamplingCoefficients.Length;
        for (int frameOffset = 0; frameOffset < samples.Length; frameOffset += channels)
        {
            int writeOffset = downsamplingHistoryFrameIndex * channels;
            for (int channel = 0; channel < channels; channel++)
                downsamplingHistory[writeOffset + channel] = samples[frameOffset + channel];

            for (int channel = 0; channel < channels; channel++)
            {
                double filtered = FilterDownsampledChannel(
                    downsamplingHistory,
                    downsamplingCoefficients,
                    channel);
                pending.Add((short)Math.Clamp(
                    Math.Round(filtered),
                    short.MinValue,
                    short.MaxValue));
            }

            downsamplingHistoryFrameIndex++;
            if (downsamplingHistoryFrameIndex == tapCount)
                downsamplingHistoryFrameIndex = 0;
        }
    }

    private double FilterDownsampledChannel(
        double[] history,
        double[] coefficients,
        int channel)
    {
        int center = coefficients.Length / 2;
        int recentFrame = downsamplingHistoryFrameIndex;
        int oldestFrame = downsamplingHistoryFrameIndex + 1;
        if (oldestFrame == coefficients.Length)
            oldestFrame = 0;

        double filtered = 0;
        for (int tap = 0; tap < center; tap++)
        {
            filtered += coefficients[tap] *
                (history[(recentFrame * channels) + channel] +
                 history[(oldestFrame * channels) + channel]);

            recentFrame--;
            if (recentFrame < 0)
                recentFrame = coefficients.Length - 1;
            oldestFrame++;
            if (oldestFrame == coefficients.Length)
                oldestFrame = 0;
        }

        return filtered +
            (coefficients[center] * history[(recentFrame * channels) + channel]);
    }

    private void InitializeDownsamplingHistory(
        ReadOnlySpan<short> samples,
        double[] history,
        int tapCount)
    {
        for (int frame = 0; frame < tapCount; frame++)
        {
            int historyOffset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                history[historyOffset + channel] = samples[channel];
        }
        downsamplingHistoryInitialized = true;
    }

    private static int CalculateDownsamplingFilterTapCount(int inputRate, int outputRate)
    {
        double scaledTapCount = DownsamplingFilterTapsPerRateRatio * inputRate / outputRate;
        int tapCount = scaledTapCount >= MaximumDownsamplingFilterTaps
            ? MaximumDownsamplingFilterTaps
            : Math.Max(MinimumDownsamplingFilterTaps, (int)Math.Ceiling(scaledTapCount));
        if ((tapCount & 1) != 0)
            return tapCount;
        return tapCount < MaximumDownsamplingFilterTaps ? tapCount + 1 : tapCount - 1;
    }

    private static double[] CreateDownsamplingFilter(
        int inputRate,
        int outputRate,
        int tapCount)
    {
        var coefficients = new double[tapCount];
        int center = tapCount / 2;
        double cutoff = DownsamplingCutoffFraction * outputRate / inputRate;
        double denominator = ModifiedBesselI0(KaiserBeta);
        double sum = 0;
        for (int tap = 0; tap < tapCount; tap++)
        {
            int offset = tap - center;
            double ideal = offset == 0
                ? 2 * cutoff
                : Math.Sin(2 * Math.PI * cutoff * offset) / (Math.PI * offset);
            double position = offset / (double)center;
            double window = ModifiedBesselI0(
                KaiserBeta * Math.Sqrt(Math.Max(0, 1 - (position * position)))) /
                denominator;
            coefficients[tap] = ideal * window;
            sum += coefficients[tap];
        }

        for (int tap = 0; tap < coefficients.Length; tap++)
            coefficients[tap] /= sum;
        return coefficients;
    }

    private static double ModifiedBesselI0(double value)
    {
        double halfSquared = value * value / 4;
        double sum = 1;
        double term = 1;
        for (int index = 1; index <= 32; index++)
        {
            term *= halfSquared / (index * index);
            sum += term;
            if (term <= sum * 1e-16)
                break;
        }
        return sum;
    }
}
