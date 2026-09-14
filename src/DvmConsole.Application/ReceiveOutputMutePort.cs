// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class ReceiveOutputMutePort(
    ReceiveMuteState state,
    Func<ChannelId, ChannelOperatorSnapshot> capture) : IReceiveOutputMutePort, ITransmitReceiveMutePort
{
    public bool IsMuted(ChannelId channelId) => state.IsMuted(channelId);

    public bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended)
        => state.ShouldEnableLivePlayback(channelId, capture(channelId).AudioEnabled, isTemporarilySuspended);

    public string? GetEffectiveReason(ChannelId channelId, bool outputMuted)
        => state.GetEffectiveReason(channelId, outputMuted);
}
