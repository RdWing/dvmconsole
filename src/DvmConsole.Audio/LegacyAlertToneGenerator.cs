namespace DvmConsole.Audio;

public enum LegacyAlertTone
{
    Alert1 = 1,
    Alert2 = 2,
    Alert3 = 3
}

// Recreates the three alert patterns bundled with the original WPF console.
// Frequencies and step boundaries are aligned to 20 ms vocoder frames so the
// generated version stays clean after DMR/P25 encoding.
public static class LegacyAlertToneGenerator
{
    public const double ToneFrequencyHz = 1000;
    public const double AlternatingHighFrequencyHz = 1500;
    public const double AlternatingLowFrequencyHz = 800;
    public const double Amplitude = 1845d / short.MaxValue;
    public static readonly TimeSpan StepDuration = TimeSpan.FromMilliseconds(240);

    public static short[] Generate(LegacyAlertTone tone)
        => Generate(tone, Amplitude);

    public static short[] Generate(LegacyAlertTone tone, double amplitude)
        => CreateSequence(tone).RenderPcm(amplitude);

    public static GeneratedToneSequence CreateSequence(LegacyAlertTone tone)
        => new(tone switch
        {
            LegacyAlertTone.Alert1 => CreateAlert1(),
            LegacyAlertTone.Alert2 => CreateAlert2(),
            LegacyAlertTone.Alert3 => CreateAlert3(),
            _ => throw new ArgumentOutOfRangeException(nameof(tone))
        });

    private static IEnumerable<GeneratedToneStep> CreateAlert1()
        => [GeneratedToneStep.Tone(ToneFrequencyHz, TimeSpan.FromSeconds(3))];

    private static IEnumerable<GeneratedToneStep> CreateAlert2()
    {
        List<GeneratedToneStep> steps = [];
        for (int cycle = 0; cycle < 7; cycle++)
        {
            steps.Add(GeneratedToneStep.Tone(AlternatingHighFrequencyHz, StepDuration));
            steps.Add(GeneratedToneStep.Tone(AlternatingLowFrequencyHz, StepDuration));
        }

        return steps;
    }

    private static IEnumerable<GeneratedToneStep> CreateAlert3()
    {
        List<GeneratedToneStep> steps = [];
        for (int pulse = 0; pulse < 8; pulse++)
        {
            steps.Add(GeneratedToneStep.Tone(ToneFrequencyHz, StepDuration));
            if (pulse < 7)
                steps.Add(GeneratedToneStep.Silence(StepDuration));
        }

        return steps;
    }
}
