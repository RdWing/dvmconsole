namespace DvmConsole.Audio;

public readonly record struct DtmfToneStep(
    char Digit,
    TimeSpan Duration,
    bool IsHold = false);

/// <summary>
/// Generates standard DTMF PCM digits and sequences without depending on an
/// audio device or UI. The caller owns transmission timing and playback.
/// </summary>
public sealed class DtmfToneGenerator
{
    private static readonly IReadOnlyDictionary<char, (double Low, double High)> Frequencies =
        new Dictionary<char, (double Low, double High)>
        {
            ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477), ['A'] = (697, 1633),
            ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477), ['B'] = (770, 1633),
            ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477), ['C'] = (852, 1633),
            ['*'] = (941, 1209), ['0'] = (941, 1336), ['#'] = (941, 1477), ['D'] = (941, 1633)
        };

    private readonly PcmToneGenerator toneGenerator;

    public DtmfToneGenerator(PcmAudioFormat? format = null)
    {
        toneGenerator = new PcmToneGenerator(format);
        Format = toneGenerator.Format;
    }

    public PcmAudioFormat Format { get; }

    public static bool IsDigit(char digit)
        => Frequencies.ContainsKey(char.ToUpperInvariant(digit));

    public short[] GenerateDigit(
        char digit,
        TimeSpan duration,
        double amplitude = 0.5)
    {
        if (!Frequencies.TryGetValue(char.ToUpperInvariant(digit), out (double Low, double High) frequency))
            throw new ArgumentOutOfRangeException(nameof(digit), $"'{digit}' is not a DTMF digit.");

        return toneGenerator.GenerateDualTone(frequency.Low, frequency.High, duration, amplitude);
    }

    public short[] GenerateSequence(
        string digits,
        TimeSpan digitDuration,
        TimeSpan interDigitSilence,
        double amplitude = 0.5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digits);
        if (interDigitSilence < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interDigitSilence));

        List<short> samples = [];
        foreach (char rawDigit in digits)
        {
            if (char.IsWhiteSpace(rawDigit))
                continue;

            char digit = char.ToUpperInvariant(rawDigit);
            if (!IsDigit(digit))
                throw new ArgumentException($"'{rawDigit}' is not a DTMF digit.", nameof(digits));

            if (samples.Count > 0 && interDigitSilence > TimeSpan.Zero)
                samples.AddRange(new short[Math.Max(1, (int)Math.Round(
                    interDigitSilence.TotalSeconds * Format.SampleRate,
                    MidpointRounding.AwayFromZero))]);
            samples.AddRange(GenerateDigit(digit, digitDuration, amplitude));
        }

        if (samples.Count == 0)
            throw new ArgumentException("The DTMF sequence contains no digits.", nameof(digits));
        return samples.ToArray();
    }

    public short[] GenerateSteps(IEnumerable<DtmfToneStep> steps, double amplitude = 0.5)
    {
        ArgumentNullException.ThrowIfNull(steps);

        List<short> samples = [];
        foreach (DtmfToneStep step in steps)
        {
            if (step.IsHold)
                samples.AddRange(toneGenerator.GenerateSilence(step.Duration));
            else
                samples.AddRange(GenerateDigit(step.Digit, step.Duration, amplitude));
        }

        if (samples.Count == 0)
            throw new ArgumentException("The DTMF preset contains no steps.", nameof(steps));
        return samples.ToArray();
    }
}
