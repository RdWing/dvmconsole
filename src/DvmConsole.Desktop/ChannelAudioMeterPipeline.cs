namespace DvmConsole.Desktop;

internal readonly record struct ChannelAudioMeterUpdate(
    ChannelViewModel Channel,
    uint StreamId,
    ChannelAudioDirection Direction,
    double Level);

// Buffers decoded PCM meter readings by audio duration rather than network
// packet cadence. DMR and P25 deliver different numbers of 20 ms codewords per
// packet, but this pipeline presents both at the same fixed UI cadence.
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
    private readonly Dictionary<(ChannelViewModel Channel, ChannelAudioDirection Direction), MeterState> states = [];

    public void Observe(
        ChannelViewModel channel,
        uint streamId,
        ReadOnlySpan<short> samples,
        ChannelAudioDirection direction)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0 || samples.IsEmpty)
            return;

        double level = ChannelAudioMeter.Calculate(samples, direction);
        lock (sync)
        {
            var key = (channel, direction);
            if (!states.TryGetValue(key, out MeterState? state))
            {
                state = new MeterState(streamId);
                states.Add(key, state);
            }
            else if (state.StreamId != streamId)
            {
                state.Reset(streamId);
            }

            state.Enqueue(level, samples.Length);
            state.TrimToMaximum(MaximumBufferedSamples);
        }
    }

    public IReadOnlyList<ChannelAudioMeterUpdate> Advance()
    {
        lock (sync)
        {
            if (states.Count == 0)
                return [];

            var updates = new List<ChannelAudioMeterUpdate>(states.Count);
            List<(ChannelViewModel Channel, ChannelAudioDirection Direction)>? completed = null;
            foreach (KeyValuePair<(ChannelViewModel Channel, ChannelAudioDirection Direction), MeterState> pair in states)
            {
                (ChannelViewModel channel, ChannelAudioDirection direction) = pair.Key;
                MeterState state = pair.Value;
                double target = state.ReadAverage(SamplesPerRefresh, out bool hadSamples);
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
                    channel,
                    state.StreamId,
                    direction,
                    state.DisplayLevel));

                if (!hadSamples && state.DisplayLevel == 0 && state.BufferedSamples == 0)
                    (completed ??= []).Add(pair.Key);
            }

            if (completed is not null)
            {
                foreach ((ChannelViewModel channel, ChannelAudioDirection direction) in completed)
                    states.Remove((channel, direction));
            }

            return updates;
        }
    }

    private sealed class MeterState(uint streamId)
    {
        private readonly Queue<MeterSegment> segments = [];

        public uint StreamId { get; private set; } = streamId;
        public int BufferedSamples { get; private set; }
        public double DisplayLevel { get; set; }

        public void Reset(uint streamId)
        {
            segments.Clear();
            BufferedSamples = 0;
            DisplayLevel = 0;
            StreamId = streamId;
        }

        public void Enqueue(double level, int sampleCount)
        {
            segments.Enqueue(new MeterSegment(level, sampleCount));
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

        public double ReadAverage(int requestedSamples, out bool hadSamples)
        {
            int remainingBudget = requestedSamples;
            int consumedSamples = 0;
            double weightedLevel = 0;
            while (remainingBudget > 0 && segments.TryPeek(out MeterSegment? segment))
            {
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

    private sealed class MeterSegment(double level, int sampleCount)
    {
        public double Level { get; } = level;
        public int RemainingSamples { get; set; } = sampleCount;
    }
}
