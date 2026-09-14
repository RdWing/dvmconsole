// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Shared PCM meter accumulation and RX-before-TX publication. The host serializes ticks.</summary>
internal sealed class ChannelAudioMeterRuntime(
    Action<ChannelAudioMeterUpdate> receive,
    Action<ChannelAudioMeterUpdate> transmit,
    TimeProvider? timeProvider = null)
{
    private ChannelAudioMeterPipeline pipeline = new(timeProvider ?? TimeProvider.System);
    private readonly ChannelAudioMeterUpdateCoalescer coalescer = new();

    // The host serializes reset with observation and publication.
    public void Reset() => pipeline = new(timeProvider ?? TimeProvider.System);

    public bool HasActivity => pipeline.HasActivity;
    public bool Observe(ChannelId channel, uint stream, ReadOnlySpan<short> samples,
        ChannelAudioDirection direction, TimeSpan delay = default)
        => pipeline.Observe(channel, stream, samples, direction, delay);

    public void Advance()
    {
        var updates = pipeline.Advance();
        foreach (var update in coalescer.CoalesceReceive(updates)) receive(update);
        foreach (var update in updates)
            if (update.Direction == ChannelAudioDirection.Transmit) transmit(update);
    }
}
