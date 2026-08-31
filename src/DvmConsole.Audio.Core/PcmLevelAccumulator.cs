namespace DvmConsole.Audio;

// Accumulates PCM energy independently of playback, protocol, and UI policy so
// callers can compare complete receive windows instead of isolated frames.
public sealed class PcmLevelAccumulator
{
    private double sumSquares;
    private long sampleCount;
    private int peak;

    public long SampleCount => sampleCount;

    public void Add(ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
        {
            double value = sample;
            sumSquares += value * value;
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        sampleCount = checked(sampleCount + samples.Length);
    }

    public bool TryMeasureAndReset(out PcmLevelMeasurement measurement)
    {
        if (sampleCount == 0)
        {
            measurement = default;
            return false;
        }

        double rms = Math.Sqrt(sumSquares / sampleCount);
        measurement = new PcmLevelMeasurement(
            sampleCount,
            ToDbfs(rms),
            ToDbfs(peak));
        Reset();
        return true;
    }

    public void Reset()
    {
        sumSquares = 0;
        sampleCount = 0;
        peak = 0;
    }

    private static double ToDbfs(double amplitude)
        => 20 * Math.Log10(Math.Max(amplitude / 32768.0, 1e-9));
}

public readonly record struct PcmLevelMeasurement(
    long SampleCount,
    double RmsDbfs,
    double PeakDbfs);
