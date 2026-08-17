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

        pending.AddRange(samples.ToArray());
        var output = new List<short>();
        while (sourcePosition + 1 < pending.Count / channels)
        {
            int frameIndex = (int)sourcePosition;
            double fraction = sourcePosition - frameIndex;
            for (int channel = 0; channel < channels; channel++)
            {
                int index = (frameIndex * channels) + channel;
                int nextIndex = index + channels;
                double value = pending[index] + ((pending[nextIndex] - pending[index]) * fraction);
                output.Add((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
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

        return output.ToArray();
    }
}
