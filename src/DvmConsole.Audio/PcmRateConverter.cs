namespace DvmConsole.Audio;

// Small streaming linear PCM rate converter used when CoreAudio exposes a
// hardware rate different from the 8 kHz voice rate.
public sealed class PcmRateConverter
{
    private readonly int inputRate;
    private readonly int outputRate;
    private readonly double step;
    private readonly List<short> pending = [];
    private double sourcePosition;

    public PcmRateConverter(int inputRate, int outputRate)
    {
        if (inputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputRate));

        this.inputRate = inputRate;
        this.outputRate = outputRate;
        step = (double)inputRate / outputRate;
    }

    public short[] Convert(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return [];
        if (inputRate == outputRate)
            return samples.ToArray();

        pending.AddRange(samples.ToArray());
        var output = new List<short>();
        while (sourcePosition + 1 < pending.Count)
        {
            int index = (int)sourcePosition;
            double fraction = sourcePosition - index;
            double value = pending[index] + ((pending[index + 1] - pending[index]) * fraction);
            output.Add((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
            sourcePosition += step;
        }

        int removable = Math.Min((int)sourcePosition, Math.Max(0, pending.Count - 1));
        if (removable > 0)
        {
            pending.RemoveRange(0, removable);
            sourcePosition -= removable;
        }

        return output.ToArray();
    }
}
