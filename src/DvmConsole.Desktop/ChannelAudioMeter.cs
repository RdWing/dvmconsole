namespace DvmConsole.Desktop;

public enum ChannelAudioDirection
{
    Receive,
    Transmit
}

// Converts 16-bit voice PCM into a visual-only card meter level. The tuning
// matches the legacy console: decoded radio audio receives more display gain
// than local microphone audio.
public static class ChannelAudioMeter
{
    private const double TransmitGain = 0.85;
    private const double ReceiveGain = 3.8;
    private const double RmsWeight = 0.72;
    private const double PeakWeight = 0.28;
    private const double NoiseFloor = 0.006;

    public static double Calculate(ReadOnlySpan<short> samples, ChannelAudioDirection direction)
    {
        if (samples.IsEmpty)
            return 0;

        double sumSquares = 0;
        double peak = 0;
        foreach (short sample in samples)
        {
            double normalized = Math.Abs(sample / 32768d);
            sumSquares += normalized * normalized;
            peak = Math.Max(peak, normalized);
        }

        double rms = Math.Sqrt(sumSquares / samples.Length);
        double blended = (rms * RmsWeight) + (peak * PeakWeight);
        double gain = direction == ChannelAudioDirection.Receive ? ReceiveGain : TransmitGain;
        return Math.Clamp((blended - NoiseFloor) * gain * 100, 0, 100);
    }
}
