// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal readonly record struct ChannelAudioMeterUpdate(
    ChannelId ChannelId,
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
    private const int MaximumTransmitBufferedSamples = SamplesPerRefresh;
    private const double MinimumVisibleLevel = 0.25;
    private static readonly double ReleaseMultiplier = Math.Pow(
        0.1,
        RefreshIntervalMilliseconds / (double)ReleaseToTenPercentMilliseconds);

    private readonly object sync = new();
    private readonly Dictionary<MeterKey, MeterState> states = [];
    private readonly List<ChannelAudioMeterUpdate> updatesScratch = [];
    private readonly List<MeterKey> completedScratch = [];
    private readonly TimeProvider timeProvider;

    public ChannelAudioMeterPipeline()
        : this(TimeProvider.System)
    {
    }

    internal ChannelAudioMeterPipeline(TimeProvider timeProvider)
        => this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public bool Observe(
        ChannelId channelId,
        uint streamId,
        ReadOnlySpan<short> samples,
        ChannelAudioDirection direction,
        TimeSpan presentationDelay = default)
    {
        if (streamId == 0 || samples.IsEmpty)
            return false;

        ChannelAudioMeterSample sample = ChannelAudioMeter.Analyze(samples);
        lock (sync)
        {
            bool wasIdle = states.Count == 0;
            var key = new MeterKey(channelId, direction, streamId);
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
            state.TrimToMaximum(direction == ChannelAudioDirection.Transmit
                ? MaximumTransmitBufferedSamples
                : MaximumBufferedSamples);
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

            updatesScratch.Clear();
            completedScratch.Clear();
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

                updatesScratch.Add(new ChannelAudioMeterUpdate(
                    key.ChannelId,
                    key.StreamId,
                    key.Direction,
                    state.DisplayLevel,
                    state.DisplayPeakLevel));

                if (!hadSamples &&
                    state.DisplayLevel == 0 &&
                    state.DisplayPeakLevel == 0 &&
                    state.BufferedSamples == 0)
                {
                    completedScratch.Add(pair.Key);
                }
            }

            foreach (MeterKey key in completedScratch)
                states.Remove(key);

            // The returned view remains valid until the next Advance call.
            return updatesScratch;
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
        ChannelId ChannelId,
        ChannelAudioDirection Direction,
        uint StreamId);

    private sealed class MeterState
    {
        private readonly Queue<MeterSegment> segments = [];
        private int headConsumedSamples;
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
            while (BufferedSamples > maximumSamples && segments.TryPeek(out MeterSegment segment))
            {
                int remaining = segment.SampleCount - headConsumedSamples;
                int count = Math.Min(BufferedSamples - maximumSamples, remaining);
                headConsumedSamples += count;
                BufferedSamples -= count;
                if (headConsumedSamples == segment.SampleCount)
                {
                    segments.Dequeue();
                    headConsumedSamples = 0;
                }
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
            while (remainingBudget > 0 && segments.TryPeek(out MeterSegment segment))
            {
                if (segment.AvailableAtTimestamp > now)
                    break;

                int count = Math.Min(
                    remainingBudget,
                    segment.SampleCount - headConsumedSamples);
                weightedMeanSquare += segment.Sample.MeanSquare * count;
                peakAmplitude = Math.Max(peakAmplitude, segment.Sample.PeakAmplitude);
                consumedSamples += count;
                remainingBudget -= count;
                BufferedSamples -= count;
                headConsumedSamples += count;
                if (headConsumedSamples == segment.SampleCount)
                {
                    segments.Dequeue();
                    headConsumedSamples = 0;
                }
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

    private readonly record struct MeterSegment(
        ChannelAudioMeterSample sample,
        int sampleCount,
        long availableAtTimestamp)
    {
        public ChannelAudioMeterSample Sample { get; } = sample;
        public int SampleCount { get; } = sampleCount;
        public long AvailableAtTimestamp { get; } = availableAtTimestamp;
    }
}

// Reuses its dictionary and output list across UI ticks. The returned view is
// callback-scoped and remains valid only until the next CoalesceReceive call.
internal sealed class ChannelAudioMeterUpdateCoalescer
{
    private readonly Dictionary<ChannelId, int> indices = [];
    private readonly List<ChannelAudioMeterUpdate> coalesced = [];

    public IReadOnlyList<ChannelAudioMeterUpdate> CoalesceReceive(
        IReadOnlyList<ChannelAudioMeterUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        indices.Clear();
        coalesced.Clear();
        for (int updateIndex = 0; updateIndex < updates.Count; updateIndex++)
        {
            ChannelAudioMeterUpdate update = updates[updateIndex];
            if (update.Direction != ChannelAudioDirection.Receive)
                continue;
            if (!indices.TryGetValue(update.ChannelId, out int index))
            {
                indices.Add(update.ChannelId, coalesced.Count);
                coalesced.Add(update);
                continue;
            }

            ChannelAudioMeterUpdate current = coalesced[index];
            coalesced[index] = current with
            {
                Level = Math.Max(current.Level, update.Level),
                PeakLevel = Math.Max(current.PeakLevel, update.PeakLevel)
            };
        }
        return coalesced;
    }
}
