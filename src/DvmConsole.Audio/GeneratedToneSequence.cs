namespace DvmConsole.Audio;

public enum GeneratedToneStepKind
{
    SingleTone,
    Dtmf,
    Silence
}

public readonly record struct GeneratedToneStep
{
    public const double MinimumSingleToneFrequencyHz = 300;
    public const double MaximumSingleToneFrequencyHz = 2500;

    private GeneratedToneStep(
        GeneratedToneStepKind kind,
        TimeSpan duration,
        double frequencyHz,
        char digit)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Kind = kind;
        Duration = AlignDuration(duration);
        FrequencyHz = frequencyHz;
        Digit = digit;
    }

    public GeneratedToneStepKind Kind { get; }
    public TimeSpan Duration { get; }
    public double FrequencyHz { get; }
    public char Digit { get; }
    public int FrameCount => checked((int)Math.Round(Duration.TotalMilliseconds / 20));

    public static GeneratedToneStep Tone(double frequencyHz, TimeSpan duration)
    {
        if (!double.IsFinite(frequencyHz) ||
            frequencyHz < MinimumSingleToneFrequencyHz ||
            frequencyHz > MaximumSingleToneFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        return new GeneratedToneStep(GeneratedToneStepKind.SingleTone, duration, frequencyHz, '\0');
    }

    public static GeneratedToneStep Dtmf(char digit, TimeSpan duration)
    {
        char normalized = char.ToUpperInvariant(digit);
        if (!DtmfToneGenerator.IsDigit(normalized))
            throw new ArgumentOutOfRangeException(nameof(digit));
        return new GeneratedToneStep(GeneratedToneStepKind.Dtmf, duration, 0, normalized);
    }

    public static GeneratedToneStep Silence(TimeSpan duration)
        => new(GeneratedToneStepKind.Silence, duration, 0, '\0');

    private static TimeSpan AlignDuration(TimeSpan duration)
    {
        double frames = duration.TotalMilliseconds / 20;
        return TimeSpan.FromMilliseconds(
            Math.Max(1, Math.Round(frames, MidpointRounding.AwayFromZero)) * 20);
    }
}

public sealed class GeneratedToneSequence
{
    private readonly GeneratedToneStep[] steps;

    public GeneratedToneSequence(IEnumerable<GeneratedToneStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        this.steps = steps.ToArray();
        if (this.steps.Length == 0)
            throw new ArgumentException("A generated tone sequence must contain at least one step.", nameof(steps));
    }

    public IReadOnlyList<GeneratedToneStep> Steps => steps;
    public int FrameCount => steps.Sum(step => step.FrameCount);
    public TimeSpan Duration => TimeSpan.FromMilliseconds(FrameCount * 20d);

    public short[] RenderPcm(double amplitude = 0.35)
    {
        var tones = new PcmToneGenerator();
        var dtmf = new DtmfToneGenerator();
        List<short> samples = [];
        foreach (GeneratedToneStep step in steps)
        {
            samples.AddRange(step.Kind switch
            {
                GeneratedToneStepKind.SingleTone => tones.GenerateTone(step.FrequencyHz, step.Duration, amplitude),
                GeneratedToneStepKind.Dtmf => dtmf.GenerateDigit(step.Digit, step.Duration, amplitude),
                GeneratedToneStepKind.Silence => tones.GenerateSilence(step.Duration),
                _ => throw new ArgumentOutOfRangeException(nameof(step.Kind))
            });
        }
        return samples.ToArray();
    }
}
