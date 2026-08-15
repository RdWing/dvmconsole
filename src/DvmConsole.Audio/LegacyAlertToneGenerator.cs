namespace DvmConsole.Audio;

public enum LegacyAlertTone
{
    Alert1 = 1,
    Alert2 = 2,
    Alert3 = 3
}

/// <summary>
/// Recreates the three alert waveforms bundled with the original WPF console.
/// The source files were 8 kHz mono PCM at approximately -25 dBFS.
/// </summary>
public static class LegacyAlertToneGenerator
{
    public const double ToneFrequencyHz = 1004;
    public const double AlternatingHighFrequencyHz = 1500;
    public const double AlternatingLowFrequencyHz = 800;
    public const double Amplitude = 1845d / short.MaxValue;
    public static readonly TimeSpan StepDuration = TimeSpan.FromMilliseconds(250);
    private const double AlternatingHighPhaseRadians = 3 * Math.PI / 4;
    private const double AlternatingLowPhaseRadians = 2 * Math.PI / 5;

    public static short[] Generate(LegacyAlertTone tone)
        => tone switch
        {
            LegacyAlertTone.Alert1 => GenerateAlert1(),
            LegacyAlertTone.Alert2 => GenerateAlert2(),
            LegacyAlertTone.Alert3 => GenerateAlert3(),
            _ => throw new ArgumentOutOfRangeException(nameof(tone))
        };

    private static short[] GenerateAlert1()
        => new PcmToneGenerator().GenerateTone(
            ToneFrequencyHz,
            TimeSpan.FromSeconds(3),
            Amplitude);

    private static short[] GenerateAlert2()
    {
        var generator = new PcmToneGenerator();
        List<short> samples = [];
        for (int cycle = 0; cycle < 7; cycle++)
        {
            samples.AddRange(generator.GenerateTone(
                AlternatingHighFrequencyHz,
                StepDuration,
                Amplitude,
                AlternatingHighPhaseRadians));
            samples.AddRange(generator.GenerateTone(
                AlternatingLowFrequencyHz,
                StepDuration,
                Amplitude,
                AlternatingLowPhaseRadians));
        }

        return samples.ToArray();
    }

    private static short[] GenerateAlert3()
    {
        var generator = new PcmToneGenerator();
        List<PcmToneStep> steps = [];
        for (int pulse = 0; pulse < 8; pulse++)
        {
            steps.Add(new PcmToneStep(ToneFrequencyHz, StepDuration));
            if (pulse < 7)
                steps.Add(new PcmToneStep(0, StepDuration, IsHold: true));
        }

        return generator.GenerateSteps(steps, Amplitude);
    }
}
