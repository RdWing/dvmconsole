using System.Buffers;

namespace DvmConsole.Audio;

// Small streaming linear PCM rate converter used when CoreAudio exposes a
// hardware rate different from the 8 kHz voice rate.
public sealed class PcmRateConverter
{
    private readonly int inputRate;
    private readonly int outputRate;
    private readonly int channels;
    private readonly double step;
    private readonly List<short> pending = [];
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

        pending.EnsureCapacity(checked(pending.Count + samples.Length));
        for (int index = 0; index < samples.Length; index++)
            pending.Add(samples[index]);

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
}
