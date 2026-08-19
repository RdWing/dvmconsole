namespace DvmConsole.Audio;

// Classifies decoded 8 kHz PCM in radio-sized frames. Only sustained,
// spectrally dominant single-frequency regions are accepted; speech, DTMF,
// silence, and uncertain transition frames remain on the ordinary voice path.
public static class PcmSingleToneAnalyzer
{
    public const int SamplesPerFrame = 160;

    private const int SampleRate = 8000;
    private const int AnalysisRadiusFrames = 1;
    private const int MinimumRunFrames = 3;
    private const double MinimumRms = 64;
    private const double MinimumPurity = 0.72;
    private const double MaximumRunFrequencyDriftHz = 60;

    public static double?[] Analyze(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return [];

        int frameCount = checked((samples.Length + SamplesPerFrame - 1) / SamplesPerFrame);
        var candidates = new double?[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
            candidates[frame] = AnalyzeFrame(samples, frame, frameCount);

        return KeepSustainedRuns(candidates);
    }

    private static double? AnalyzeFrame(
        ReadOnlySpan<short> samples,
        int frameIndex,
        int frameCount)
    {
        int firstFrame = Math.Max(0, frameIndex - AnalysisRadiusFrames);
        int lastFrameExclusive = Math.Min(frameCount, frameIndex + AnalysisRadiusFrames + 1);
        int start = firstFrame * SamplesPerFrame;
        int end = Math.Min(samples.Length, lastFrameExclusive * SamplesPerFrame);
        int count = end - start;
        if (count < SamplesPerFrame * 2)
            return null;

        double mean = 0;
        for (int index = start; index < end; index++)
            mean += samples[index];
        mean /= count;

        var centered = new double[count];
        double totalEnergy = 0;
        for (int index = 0; index < count; index++)
        {
            double value = samples[start + index] - mean;
            centered[index] = value;
            totalEnergy += value * value;
        }

        if (Math.Sqrt(totalEnergy / count) < MinimumRms)
            return null;

        double estimatedFrequency = EstimateFromZeroCrossings(centered);
        if (estimatedFrequency < GeneratedToneStep.MinimumSingleToneFrequencyHz - 100 ||
            estimatedFrequency > GeneratedToneStep.MaximumSingleToneFrequencyHz + 100)
        {
            return null;
        }

        double searchStart = Math.Max(
            GeneratedToneStep.MinimumSingleToneFrequencyHz,
            estimatedFrequency - 80);
        double searchEnd = Math.Min(
            GeneratedToneStep.MaximumSingleToneFrequencyHz,
            estimatedFrequency + 80);
        double bestFrequency = searchStart;
        double bestEnergy = 0;
        for (double frequency = searchStart; frequency <= searchEnd; frequency += 1)
        {
            double energy = MeasureSinusoidEnergy(centered, frequency);
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestFrequency = frequency;
            }
        }

        double purity = Math.Min(1, bestEnergy / totalEnergy);
        return purity >= MinimumPurity ? bestFrequency : null;
    }

    private static double EstimateFromZeroCrossings(ReadOnlySpan<double> samples)
    {
        int crossings = 0;
        for (int index = 1; index < samples.Length; index++)
        {
            if ((samples[index - 1] < 0 && samples[index] >= 0) ||
                (samples[index - 1] >= 0 && samples[index] < 0))
            {
                crossings++;
            }
        }

        double durationSeconds = (samples.Length - 1d) / SampleRate;
        return crossings / (2 * durationSeconds);
    }

    private static double MeasureSinusoidEnergy(ReadOnlySpan<double> samples, double frequency)
    {
        double angularStep = 2 * Math.PI * frequency / SampleRate;
        double sine = 0;
        double cosine = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            double angle = angularStep * index;
            sine += samples[index] * Math.Sin(angle);
            cosine += samples[index] * Math.Cos(angle);
        }

        return 2 * ((sine * sine) + (cosine * cosine)) / samples.Length;
    }

    private static double?[] KeepSustainedRuns(double?[] candidates)
    {
        var detected = new double?[candidates.Length];
        int index = 0;
        while (index < candidates.Length)
        {
            if (candidates[index] is not double firstFrequency)
            {
                index++;
                continue;
            }

            int start = index;
            double previousFrequency = firstFrequency;
            index++;
            while (index < candidates.Length &&
                   candidates[index] is double nextFrequency &&
                   Math.Abs(nextFrequency - previousFrequency) <= MaximumRunFrequencyDriftHz)
            {
                previousFrequency = nextFrequency;
                index++;
            }

            int count = index - start;
            if (count < MinimumRunFrames)
                continue;

            double[] run = candidates[start..index]
                .Select(static frequency => frequency!.Value)
                .Order()
                .ToArray();
            double representativeFrequency = run[run.Length / 2];
            for (int detectedIndex = start; detectedIndex < index; detectedIndex++)
                detected[detectedIndex] = representativeFrequency;
        }

        return detected;
    }
}
