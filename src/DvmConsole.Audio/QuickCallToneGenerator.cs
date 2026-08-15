namespace DvmConsole.Audio;

/// <summary>
/// Builds the fixed-duration two-tone sequence used by a Quick Call II page.
/// The legacy console sends tone A for one second followed by tone B for three
/// seconds; keeping those timings here makes the behavior testable without a
/// live FNE or audio device.
/// </summary>
public static class QuickCallToneGenerator
{
    public const double MinimumFrequencyHz = 1;
    public const double MaximumFrequencyHzExclusive = 4000;
    public static readonly TimeSpan ToneADuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ToneBDuration = TimeSpan.FromSeconds(3);

    public static short[] Generate(
        double toneAFrequencyHz,
        double toneBFrequencyHz,
        double amplitude = 0.35)
    {
        ValidateFrequency(toneAFrequencyHz, nameof(toneAFrequencyHz));
        ValidateFrequency(toneBFrequencyHz, nameof(toneBFrequencyHz));
        return new PcmToneGenerator().GenerateSteps(
        [
            new PcmToneStep(toneAFrequencyHz, ToneADuration),
            new PcmToneStep(toneBFrequencyHz, ToneBDuration)
        ],
        amplitude);
    }

    public static bool TryParse(
        string? toneAText,
        string? toneBText,
        out double toneAFrequencyHz,
        out double toneBFrequencyHz,
        out string? error)
    {
        toneAFrequencyHz = 0;
        toneBFrequencyHz = 0;
        error = null;
        if (!double.TryParse(
                toneAText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out toneAFrequencyHz) ||
            !double.TryParse(
                toneBText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out toneBFrequencyHz) ||
            !IsValidFrequency(toneAFrequencyHz) ||
            !IsValidFrequency(toneBFrequencyHz))
        {
            error = "QCII tone A and B frequencies must each be 1–3999 Hz.";
            return false;
        }

        return true;
    }

    private static bool IsValidFrequency(double frequency)
        => double.IsFinite(frequency) &&
           frequency >= MinimumFrequencyHz &&
           frequency < MaximumFrequencyHzExclusive;

    private static void ValidateFrequency(double frequency, string parameterName)
    {
        if (!IsValidFrequency(frequency))
            throw new ArgumentOutOfRangeException(parameterName, "The QCII frequency must be 1–3999 Hz.");
    }
}
