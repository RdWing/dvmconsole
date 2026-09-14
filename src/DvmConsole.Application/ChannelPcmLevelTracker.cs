// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

/// <summary>Accumulates diagnostic PCM windows independently for each channel and direction.</summary>
internal sealed class ChannelPcmLevelTracker(int windowSamples)
{
    private readonly object sync = new();
    private readonly Dictionary<(ChannelId Channel, ChannelAudioDirection Direction), StreamLevels> levels = [];

    public IReadOnlyList<PcmLevelMeasurement> Observe(ChannelId channel, ChannelAudioDirection direction,
        uint streamId, ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return [];
        lock (sync)
        {
            var key = (channel, direction);
            if (!levels.TryGetValue(key, out var stream))
                levels.Add(key, stream = new(streamId, windowSamples));
            else if (streamId != 0 && stream.StreamId != streamId)
            {
                stream.StreamId = streamId;
                stream.Accumulator.Reset();
            }
            return stream.Accumulator.Observe(samples);
        }
    }

    public void Clear()
    {
        lock (sync) levels.Clear();
    }

    private sealed class StreamLevels(uint streamId, int windowSamples)
    {
        public uint StreamId { get; set; } = streamId;
        public PcmLevelWindowAccumulator Accumulator { get; } = new(windowSamples);
    }
}
