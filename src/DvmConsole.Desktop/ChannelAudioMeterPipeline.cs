using System.Diagnostics;

namespace DvmConsole.Desktop;

internal readonly record struct ChannelAudioMeterUpdate(
    ChannelViewModel Channel,
    uint StreamId,
    ChannelAudioDirection Direction,
    double Level);

// Buffers PCM meter readings by audio duration rather than network packet
// cadence. Receive observations arrive when frames enter the physical output
// queue and remain held until their estimated presentation time.
internal sealed class ChannelAudioMeterPipeline
{
    internal const int RefreshIntervalMilliseconds = 50;
    private const int VoiceSampleRate = 8_000;
    private const int SamplesPerRefresh = VoiceSampleRate * RefreshIntervalMilliseconds / 1_000;
    private const int MaximumBufferedSamples = VoiceSampleRate * 240 / 1_000;
    private const double ReleaseBlend = 0.55;
    private const double IdleDecay = 0.55;
    private const double MinimumVisibleLevel = 0.25;

    private readonly object sync = new();
    private readonly Dictionary<MeterKey, MeterState> states = [];

    public bool Observe(
        ChannelViewModel channel,
        uint streamId,
        ReadOnlySpan<short> samples,
        ChannelAudioDirection direction,
        TimeSpan presentationDelay = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0 || samples.IsEmpty)
            return false;

        double level = ChannelAudioMeter.Calculate(samples, direction);
        lock (sync)
        {
            bool wasIdle = states.Count == 0;
            var key = new MeterKey(channel, direction, streamId);
            if (!states.TryGetValue(key, out MeterState? state))
            {
                state = new MeterState();
                states.Add(key, state);
            }

            long delayTicks = presentationDelay <= TimeSpan.Zero
                ? 0
                : (long)(presentationDelay.TotalSeconds * Stopwatch.Frequency);
            state.Enqueue(
                level,
                samples.Length,
                checked(Stopwatch.GetTimestamp() + delayTicks));
            state.TrimToMaximum(MaximumBufferedSamples);
            return wasIdle;
        }
    }

    public bool HasActivity
    {
        get
        {
            lock (sync)
                return states.Count > 0;
        }
    }

    public IReadOnlyList<ChannelAudioMeterUpdate> Advance()
    {
        lock (sync)
        {
            if (states.Count == 0)
                return [];

            var updates = new List<ChannelAudioMeterUpdate>(states.Count);
            List<MeterKey>? completed = null;
            long now = Stopwatch.GetTimestamp();
            foreach (KeyValuePair<MeterKey, MeterState> pair in states)
            {
                MeterKey key = pair.Key;
                MeterState state = pair.Value;
                double target = state.ReadAverage(SamplesPerRefresh, now, out bool hadSamples);
                if (hadSamples)
                {
                    state.DisplayLevel = target >= state.DisplayLevel
                        ? target
                        : (state.DisplayLevel * ReleaseBlend) + (target * (1 - ReleaseBlend));
                }
                else
                {
                    state.DisplayLevel *= IdleDecay;
                    if (state.DisplayLevel < MinimumVisibleLevel)
                        state.DisplayLevel = 0;
                }

                updates.Add(new ChannelAudioMeterUpdate(
                    key.Channel,
                    key.StreamId,
                    key.Direction,
                    state.DisplayLevel));

                if (!hadSamples && state.DisplayLevel == 0 && state.BufferedSamples == 0)
                    (completed ??= []).Add(pair.Key);
            }

            if (completed is not null)
            {
                foreach (MeterKey key in completed)
                    states.Remove(key);
            }

            return updates;
        }
    }

    private readonly record struct MeterKey(
        ChannelViewModel Channel,
        ChannelAudioDirection Direction,
        uint StreamId);

    private sealed class MeterState
    {
        private readonly Queue<MeterSegment> segments = [];

        public int BufferedSamples { get; private set; }
        public double DisplayLevel { get; set; }

        public void Enqueue(double level, int sampleCount, long availableAtTimestamp)
        {
            segments.Enqueue(new MeterSegment(level, sampleCount, availableAtTimestamp));
            BufferedSamples = checked(BufferedSamples + sampleCount);
        }

        public void TrimToMaximum(int maximumSamples)
        {
            while (BufferedSamples > maximumSamples && segments.TryPeek(out MeterSegment? segment))
            {
                int count = Math.Min(BufferedSamples - maximumSamples, segment.RemainingSamples);
                segment.RemainingSamples -= count;
                BufferedSamples -= count;
                if (segment.RemainingSamples == 0)
                    segments.Dequeue();
            }
        }

        public double ReadAverage(
            int requestedSamples,
            long now,
            out bool hadSamples)
        {
            int remainingBudget = requestedSamples;
            int consumedSamples = 0;
            double weightedLevel = 0;
            while (remainingBudget > 0 && segments.TryPeek(out MeterSegment? segment))
            {
                if (segment.AvailableAtTimestamp > now)
                    break;

                int count = Math.Min(remainingBudget, segment.RemainingSamples);
                weightedLevel += segment.Level * count;
                consumedSamples += count;
                remainingBudget -= count;
                BufferedSamples -= count;
                segment.RemainingSamples -= count;
                if (segment.RemainingSamples == 0)
                    segments.Dequeue();
            }

            hadSamples = consumedSamples > 0;
            return hadSamples ? weightedLevel / consumedSamples : 0;
        }
    }

    private sealed class MeterSegment(
        double level,
        int sampleCount,
        long availableAtTimestamp)
    {
        public double Level { get; } = level;
        public int RemainingSamples { get; set; } = sampleCount;
        public long AvailableAtTimestamp { get; } = availableAtTimestamp;
    }
}
