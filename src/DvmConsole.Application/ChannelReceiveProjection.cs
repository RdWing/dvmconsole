// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal readonly record struct ChannelReceiveProjectionResult(
    ReceiveStreamDecision? Decision,
    bool EncryptionReset = false,
    bool EncryptionChanged = false,
    bool PlaybackEnded = false);

/// <summary>Applies captured routing decisions to authoritative channel state.</summary>
internal static class ChannelReceiveProjection
{
    public static ChannelReceiveProjectionResult Apply(
        ConsoleChannelState channel, string systemName, IRadioMediaFrame traffic,
        DateTimeOffset now, ReceiveIngressRouteDecision ingress)
    {
        if (ingress.RouteKey != channel.Identity.RouteKey ||
            !channel.Receive.CanProjectTraffic(systemName, traffic,
                ingress.ActiveStreamIds.Contains(traffic.StreamId)))
            return default;
        return Apply(channel, traffic, now, ingress.PacketDecision);
    }

    public static ChannelReceiveProjectionResult Apply(
        ConsoleChannelState channel, IRadioMediaFrame traffic,
        DateTimeOffset now, ReceiveRouteProjectionDecision projection)
    {
        (bool reset, bool changed) = channel.Receive.ObserveEncryption(traffic);
        return new(channel.Receive.ApplyProjection(traffic, now, projection), reset, changed);
    }

    public static ChannelReceiveProjectionResult Advance(
        ConsoleChannelState channel, ReceiveRouteProjectionDecision projection, DateTimeOffset now)
    {
        channel.Receive.SetProjection(projection.ActiveStreamIds);
        ReceiveStreamDecision decision = projection.StreamDecision;
        bool playbackEnded = false;
        if (decision.Transition is ReceiveStreamTransition.GraceExpired or
                ReceiveStreamTransition.TerminationExpired &&
            decision.EndedStreamId is uint endedStreamId)
        {
            if (channel.Runtime.StreamId == endedStreamId)
                channel.Runtime.MarkIdle(now);
            channel.Receive.EndMeter(endedStreamId);
            playbackEnded = channel.Receive.EndPlayback(endedStreamId);
        }
        return new(decision, PlaybackEnded: playbackEnded);
    }
}
