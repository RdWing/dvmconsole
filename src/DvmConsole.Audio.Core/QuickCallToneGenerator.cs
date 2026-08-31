namespace DvmConsole.Audio;

// Builds the fixed-duration two-tone sequence used by a Quick Call II page.
// The legacy console sends tone A for one second followed by tone B for three
// seconds; keeping those timings here makes the behavior testable without a
// live FNE or audio device.
public static class QuickCallToneGenerator
{
    public const double MinimumFrequencyHz = 300;
    public const double MaximumFrequencyHz = 2500;
    public static readonly TimeSpan TransmitLeadIn = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan ToneADuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ToneBDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TransmitTail = TimeSpan.FromMilliseconds(750);

    public static short[] Generate(
        double toneAFrequencyHz,
        double toneBFrequencyHz,
        double amplitude = 0.35)
        => CreateSequence(toneAFrequencyHz, toneBFrequencyHz).RenderPcm(amplitude);

    public static GeneratedToneSequence CreateSequence(
        double toneAFrequencyHz,
        double toneBFrequencyHz)
    {
        ValidateFrequency(toneAFrequencyHz, nameof(toneAFrequencyHz));
        ValidateFrequency(toneBFrequencyHz, nameof(toneBFrequencyHz));
        return new GeneratedToneSequence(
        [
            GeneratedToneStep.Silence(TransmitLeadIn),
            GeneratedToneStep.Tone(toneAFrequencyHz, ToneADuration),
            GeneratedToneStep.Tone(toneBFrequencyHz, ToneBDuration),
            GeneratedToneStep.Silence(TransmitTail)
        ]);
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
            error = "QCII tone A and B frequencies must each be 300–2500 Hz.";
            return false;
        }

        return true;
    }

    private static bool IsValidFrequency(double frequency)
        => double.IsFinite(frequency) &&
           frequency >= MinimumFrequencyHz &&
           frequency <= MaximumFrequencyHz;

    private static void ValidateFrequency(double frequency, string parameterName)
    {
        if (!IsValidFrequency(frequency))
            throw new ArgumentOutOfRangeException(parameterName, "The QCII frequency must be 300–2500 Hz.");
    }
}
