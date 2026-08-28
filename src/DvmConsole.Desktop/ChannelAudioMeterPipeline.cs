namespace DvmConsole.Desktop;

internal readonly record struct ChannelAudioMeterUpdate(
    ChannelViewModel Channel,
    uint StreamId,
    ChannelAudioDirection Direction,
    double Level,
    double PeakLevel);

// Buffers PCM meter readings by audio duration rather than network packet
// cadence. Receive observations arrive when frames enter the physical output
// queue and remain held until their estimated presentation time.
internal sealed class ChannelAudioMeterPipeline
{
    internal const int RefreshIntervalMilliseconds = 50;
    internal const int PeakHoldMilliseconds = 750;
    internal const int ReleaseToTenPercentMilliseconds = 500;
    private const int VoiceSampleRate = 8_000;
    private const int SamplesPerRefresh = VoiceSampleRate * RefreshIntervalMilliseconds / 1_000;
    private const int MaximumBufferedSamples = VoiceSampleRate * 240 / 1_000;
    private const double MinimumVisibleLevel = 0.25;
    private static readonly double ReleaseMultiplier = Math.Pow(
        0.1,
        RefreshIntervalMilliseconds / (double)ReleaseToTenPercentMilliseconds);

    private readonly object sync = new();
    private readonly Dictionary<MeterKey, MeterState> states = [];
    private readonly TimeProvider timeProvider;

    public ChannelAudioMeterPipeline()
        : this(TimeProvider.System)
    {
    }

    internal ChannelAudioMeterPipeline(TimeProvider timeProvider)
        => this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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

        ChannelAudioMeterSample sample = ChannelAudioMeter.Analyze(samples);
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
                : (long)(presentationDelay.TotalSeconds * timeProvider.TimestampFrequency);
            state.Enqueue(
                sample,
                samples.Length,
                checked(timeProvider.GetTimestamp() + delayTicks));
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
            long now = timeProvider.GetTimestamp();
            foreach (KeyValuePair<MeterKey, MeterState> pair in states)
            {
                MeterKey key = pair.Key;
                MeterState state = pair.Value;
                ChannelAudioMeterLevels target = state.ReadWindow(
                    SamplesPerRefresh,
                    now,
                    out bool hadSamples);
                state.Apply(target, now, timeProvider.TimestampFrequency);

                updates.Add(new ChannelAudioMeterUpdate(
                    key.Channel,
                    key.StreamId,
                    key.Direction,
                    state.DisplayLevel,
                    state.DisplayPeakLevel));

                if (!hadSamples &&
                    state.DisplayLevel == 0 &&
                    state.DisplayPeakLevel == 0 &&
                    state.BufferedSamples == 0)
                {
                    (completed ??= []).Add(pair.Key);
                }
            }

            if (completed is not null)
            {
                foreach (MeterKey key in completed)
                    states.Remove(key);
            }

            return updates;
        }
    }

    private static double ApplyBallistics(double current, double target)
    {
        if (target >= current)
            return target;

        double next = target + ((current - target) * ReleaseMultiplier);
        return next < MinimumVisibleLevel ? 0 : next;
    }

    private readonly record struct MeterKey(
        ChannelViewModel Channel,
        ChannelAudioDirection Direction,
        uint StreamId);

    private sealed class MeterState
    {
        private readonly Queue<MeterSegment> segments = [];
        private long peakHoldUntil;

        public int BufferedSamples { get; private set; }
        public double DisplayLevel { get; private set; }
        public double DisplayPeakLevel { get; private set; }

        public void Enqueue(
            ChannelAudioMeterSample sample,
            int sampleCount,
            long availableAtTimestamp)
        {
            segments.Enqueue(new MeterSegment(sample, sampleCount, availableAtTimestamp));
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

        public ChannelAudioMeterLevels ReadWindow(
            int requestedSamples,
            long now,
            out bool hadSamples)
        {
            int remainingBudget = requestedSamples;
            int consumedSamples = 0;
            double weightedMeanSquare = 0;
            double peakAmplitude = 0;
            while (remainingBudget > 0 && segments.TryPeek(out MeterSegment? segment))
            {
                if (segment.AvailableAtTimestamp > now)
                    break;

                int count = Math.Min(remainingBudget, segment.RemainingSamples);
                weightedMeanSquare += segment.Sample.MeanSquare * count;
                peakAmplitude = Math.Max(peakAmplitude, segment.Sample.PeakAmplitude);
                consumedSamples += count;
                remainingBudget -= count;
                BufferedSamples -= count;
                segment.RemainingSamples -= count;
                if (segment.RemainingSamples == 0)
                    segments.Dequeue();
            }

            hadSamples = consumedSamples > 0;
            return hadSamples
                ? ChannelAudioMeter.Scale(new ChannelAudioMeterSample(
                    weightedMeanSquare / consumedSamples,
                    peakAmplitude))
                : default;
        }

        public void Apply(ChannelAudioMeterLevels target, long now, long timestampFrequency)
        {
            DisplayLevel = ApplyBallistics(DisplayLevel, target.Rms);

            if (target.Peak >= DisplayPeakLevel)
            {
                DisplayPeakLevel = target.Peak;
                peakHoldUntil = checked(now +
                    (long)(PeakHoldMilliseconds / 1_000d * timestampFrequency));
            }
            else if (now >= peakHoldUntil)
            {
                DisplayPeakLevel = ApplyBallistics(DisplayPeakLevel, target.Peak);
            }
        }
    }

    private sealed class MeterSegment(
        ChannelAudioMeterSample sample,
        int sampleCount,
        long availableAtTimestamp)
    {
        public ChannelAudioMeterSample Sample { get; } = sample;
        public int RemainingSamples { get; set; } = sampleCount;
        public long AvailableAtTimestamp { get; } = availableAtTimestamp;
    }
}
