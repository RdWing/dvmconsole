using DvmConsole.Audio;

namespace DvmConsole.Application;

// Splits arbitrary codec frame sizes into exact measurement windows and
// carries any remainder forward. This keeps level diagnostics comparable
// across protocols whose frame batches do not divide evenly into one second.
internal sealed class PcmLevelWindowAccumulator
{
    private readonly int windowSamples;
    private readonly PcmLevelAccumulator levels = new();

    public PcmLevelWindowAccumulator(int windowSamples)
    {
        if (windowSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSamples));
        this.windowSamples = windowSamples;
    }

    public long PendingSamples => levels.SampleCount;

    public IReadOnlyList<PcmLevelMeasurement> Observe(ReadOnlySpan<short> samples)
    {
        List<PcmLevelMeasurement>? completed = null;
        while (!samples.IsEmpty)
        {
            int remainingInWindow = checked(windowSamples - (int)levels.SampleCount);
            int acceptedSamples = Math.Min(samples.Length, remainingInWindow);
            levels.Add(samples[..acceptedSamples]);
            samples = samples[acceptedSamples..];

            if (levels.SampleCount != windowSamples)
                continue;

            if (levels.TryMeasureAndReset(out PcmLevelMeasurement measurement))
                (completed ??= []).Add(measurement);
        }

        return completed is null
            ? Array.Empty<PcmLevelMeasurement>()
            : completed;
    }

    public void Reset()
        => levels.Reset();
}
