// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    /// <summary>Projects audible receive and active transmit meters into authoritative channel state.</summary>
    public void InitializePresentedMeters()
        => InitializeMeters(ApplyPresentedReceiveMeter, ApplyPresentedTransmitMeter);

    private void ApplyPresentedReceiveMeter(ChannelAudioMeterUpdate update)
    {
        ConsoleChannelState channel = Channels[update.ChannelId];
        var levels = ChannelAudioMeterProjection.ProjectPresentedReceive(channel, update.Level, update.PeakLevel);
        channel.Meter.Update(levels.Rms, levels.Peak, minimumChange: 0.25);
    }

    private void ApplyPresentedTransmitMeter(ChannelAudioMeterUpdate update)
    {
        ConsoleChannelState channel = Channels[update.ChannelId];
        if (ChannelAudioMeterProjection.Project(channel, update.Level, update.PeakLevel,
            update.Direction, update.StreamId) is { } levels)
            channel.Meter.Update(levels.Rms, levels.Peak, minimumChange: 0.25);
    }
}
