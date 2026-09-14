// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

internal static class ChannelAudioMeterProjection
{
    public static ChannelAudioMeterLevels? Project(ConsoleChannelState channel, double level,
        double? peak = null, ChannelAudioDirection? direction = null, uint? streamId = null)
    {
        long receiveStream = channel.Receive.MeterStreamId;
        if (streamId is uint expected && channel.PresentedStreamId != expected &&
            !(direction == ChannelAudioDirection.Receive && receiveStream == expected)) return null;
        var selection = channel.Operator.Snapshot;
        bool fastReceive = direction == ChannelAudioDirection.Receive && streamId is uint received && receiveStream == received;
        if ((direction == ChannelAudioDirection.Receive &&
                (!selection.AudioEnabled || selection.AudioSuspended ||
                 (channel.ReceivePresentationOwner is null && !fastReceive))) ||
            (direction == ChannelAudioDirection.Transmit && channel.Runtime.State != ChannelRuntimeState.Transmitting))
            return default(ChannelAudioMeterLevels);
        return Normalize(level, peak);
    }

    // Mixer-presented RX can outlive the physical stream currently shown by the
    // channel. Do not reject that audible tail using its original stream ID.
    public static ChannelAudioMeterLevels ProjectPresentedReceive(ConsoleChannelState channel, double level, double? peak = null)
    {
        bool active = channel.ReceivePresentationOwner is not null || channel.Receive.MeterStreamId != 0;
        return active ? ProjectReceiveSelection(channel, level, peak) : default;
    }

    public static ChannelAudioMeterLevels ProjectReceiveSelection(ConsoleChannelState channel, double level, double? peak = null)
    {
        var selection = channel.Operator.Snapshot;
        return selection.AudioEnabled && !selection.AudioSuspended ? Normalize(level, peak) : default;
    }

    private static ChannelAudioMeterLevels Normalize(double level, double? peak)
    {
        double normalized = double.IsFinite(level) ? Math.Clamp(level, 0, 100) : 0;
        return new(normalized, peak is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : normalized);
    }
}
