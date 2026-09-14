// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession
{
    private IScheduledWork? meterWork;
    private ChannelAudioMeterRuntime meters => operationalRuntime.Meters;

    internal void ObservePresentedSamples(ChannelId channel, uint stream, ReadOnlyMemory<short> samples, TimeSpan delay)
    {
        lock (ingressSync)
        {
            if (IsStopping || audioUnavailable || !state.Channels[channel].Operator.Snapshot.AudioEnabled) return;
            state.Channels[channel].MarkReceiveMeter(stream, ended: false);
            if (meters.Observe(channel, stream, samples.Span, ChannelAudioDirection.Receive, delay)) meterWork?.Start();
        }
    }

    internal void AdvanceMeters()
    {
        lock (ingressSync)
        {
            if (IsStopping || !meters.HasActivity) return;
            meters.Advance();
            if (!meters.HasActivity) meterWork?.Stop();
        }
    }

    private void ApplyReceiveMeter(ChannelAudioMeterUpdate update)
    {
        var channel = state.Channels[update.ChannelId];
        if (channel.Operator.Snapshot.TransmitEnabled) return;
        var levels = audioUnavailable ? default : ChannelAudioMeterProjection.ProjectReceiveSelection(channel, update.Level, update.PeakLevel);
        PublishMeter(update.ChannelId, levels.Rms, levels.Peak);
    }

    private void ApplyTransmitMeter(ChannelAudioMeterUpdate update)
    {
        var channel = state.Channels[update.ChannelId];
        if (channel.Operator.Snapshot.TransmitEnabled && channel.Runtime.StreamId == update.StreamId)
            PublishMeter(update.ChannelId, update.Level, update.PeakLevel);
    }

    private void PublishMeter(ChannelId channel, double level, double peak)
    {
        state.Channels[channel].Meter.Update(level, peak);
        var sample = new ChannelMeterSample(channel, level, peak, dependencies.Host.Clock.UtcNow);
        foreach (EventHandler<ChannelMeterSample> observer in MeterSampled?.GetInvocationList() ?? [])
        {
            try { observer(this, sample); }
            catch { /* Presentation failures cannot interrupt audio processing. */ }
        }
    }

    private void ResetMeters()
    {
        // Discard queued readings across interruptions/replacement, just like audio.
        meterWork?.Stop();
        meters.Reset();
        foreach (var channel in state.Channels.Keys) PublishMeter(channel, 0, 0);
    }
}
