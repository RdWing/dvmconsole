namespace DvmConsole.Application;

public enum ChannelAudioDirection
{
    Receive,
    Transmit
}

public readonly record struct ChannelAudioMeterLevels(
    double Rms,
    double Peak);

internal readonly record struct ChannelAudioMeterSample(
    double MeanSquare,
    double PeakAmplitude);

// Maps 16-bit voice PCM onto one calibrated digital scale for both receive
// and transmit. The visible range is -50 to 0 dBFS, so the nominal -25 dBFS
// speech target sits at the center of the card meter.
public static class ChannelAudioMeter
{
    internal const double MinimumDbfs = -50;
    internal const double MaximumDbfs = 0;
    internal const double YellowThresholdDisplayLevel = 76;
    internal const double RedThresholdDisplayLevel = 88;

    public static ChannelAudioMeterLevels Measure(ReadOnlySpan<short> samples)
        => Scale(Analyze(samples));

    internal static ChannelAudioMeterSample Analyze(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return default;

        double sumSquares = 0;
        double peak = 0;
        foreach (short sample in samples)
        {
            double amplitude = Math.Abs(sample / 32768d);
            sumSquares += amplitude * amplitude;
            peak = Math.Max(peak, amplitude);
        }

        return new ChannelAudioMeterSample(
            sumSquares / samples.Length,
            peak);
    }

    internal static ChannelAudioMeterLevels Scale(ChannelAudioMeterSample sample)
        => new(
            ToDisplayLevel(Math.Sqrt(Math.Max(0, sample.MeanSquare))),
            ToDisplayLevel(sample.PeakAmplitude));

    internal static double ToDisplayLevel(double amplitude)
    {
        if (!double.IsFinite(amplitude) || amplitude <= 0)
            return 0;

        double dbfs = 20 * Math.Log10(Math.Min(amplitude, 1));
        return Math.Clamp(
            (dbfs - MinimumDbfs) / (MaximumDbfs - MinimumDbfs) * 100,
            0,
            100);
    }
}
