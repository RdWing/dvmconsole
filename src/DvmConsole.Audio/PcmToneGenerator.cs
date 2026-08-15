namespace DvmConsole.Audio;

public readonly record struct PcmToneStep(
    double FrequencyHz,
    TimeSpan Duration,
    bool IsHold = false);

/// <summary>
/// Generates bounded mono PCM tones for alerts and future DTMF/preset playback.
/// The generator has no device or UI dependency.
/// </summary>
public sealed class PcmToneGenerator
{
    public PcmToneGenerator(PcmAudioFormat? format = null)
    {
        Format = format ?? PcmAudioFormat.Voice8KhzMono16Bit;
        if (Format.Channels != 1 || Format.BitsPerSample != 16)
            throw new NotSupportedException("Tone generation currently requires mono 16-bit PCM.");
    }

    public PcmAudioFormat Format { get; }

    public short[] GenerateTone(
        double frequency,
        TimeSpan duration,
        double amplitude = 0.5,
        double phaseRadians = 0)
    {
        ValidateTone(frequency, duration, amplitude);
        if (!double.IsFinite(phaseRadians))
            throw new ArgumentOutOfRangeException(nameof(phaseRadians));
        int sampleCount = GetSampleCount(duration);
        short[] samples = new short[sampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            double time = (double)index / Format.SampleRate;
            samples[index] = ToSample(Math.Sin((2 * Math.PI * frequency * time) + phaseRadians) * amplitude);
        }

        return samples;
    }

    public short[] GenerateDualTone(
        double lowFrequency,
        double highFrequency,
        TimeSpan duration,
        double amplitude = 0.5)
    {
        ValidateTone(lowFrequency, duration, amplitude);
        ValidateTone(highFrequency, duration, amplitude);
        if (lowFrequency == highFrequency)
            return GenerateTone(lowFrequency, duration, amplitude);

        int sampleCount = GetSampleCount(duration);
        short[] samples = new short[sampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            double time = (double)index / Format.SampleRate;
            double value = (Math.Sin(2 * Math.PI * lowFrequency * time) +
                            Math.Sin(2 * Math.PI * highFrequency * time)) * amplitude / 2;
            samples[index] = ToSample(value);
        }

        return samples;
    }

    public short[] GenerateSteps(IEnumerable<PcmToneStep> steps, double amplitude = 0.5)
    {
        ArgumentNullException.ThrowIfNull(steps);

        List<short> samples = [];
        foreach (PcmToneStep step in steps)
        {
            if (step.IsHold)
                samples.AddRange(GenerateSilence(step.Duration));
            else
                samples.AddRange(GenerateTone(step.FrequencyHz, step.Duration, amplitude));
        }

        if (samples.Count == 0)
            throw new ArgumentException("The tone preset contains no steps.", nameof(steps));
        return samples.ToArray();
    }

    public short[] GenerateSilence(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return new short[GetSampleCount(duration)];
    }

    private int GetSampleCount(TimeSpan duration)
    {
        double sampleCount = duration.TotalSeconds * Format.SampleRate;
        if (sampleCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration), "The tone is too long.");
        return Math.Max(1, (int)Math.Round(sampleCount, MidpointRounding.AwayFromZero));
    }

    private void ValidateTone(double frequency, TimeSpan duration, double amplitude)
    {
        if (double.IsNaN(frequency) || double.IsInfinity(frequency) || frequency <= 0 || frequency >= Format.SampleRate / 2d)
            throw new ArgumentOutOfRangeException(nameof(frequency), "The tone frequency must be below the Nyquist frequency.");
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (double.IsNaN(amplitude) || double.IsInfinity(amplitude) || amplitude is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(amplitude));
    }

    private static short ToSample(double value)
        => (short)Math.Clamp(
            Math.Round(value * short.MaxValue, MidpointRounding.AwayFromZero),
            short.MinValue,
            short.MaxValue);
}
