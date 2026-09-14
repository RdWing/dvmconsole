// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Operations;

namespace DvmConsole.Application;

// Immutable, owner-independent result of reducing one packet for one route.
// Presentation objects never escape through this boundary, so audio, patch,
// and delayed UI consumers can replay the same ingress decision without
// advancing receive lifecycle state a second time.
internal readonly record struct ReceiveRouteProjectionDecision(
    ChannelRouteKey RouteKey,
    ReceiveAction Actions,
    ReceiveStreamDecision StreamDecision,
    ImmutableHashSet<uint> ActiveStreamIds)
{
    public uint PrimaryStreamId => StreamDecision.ActiveStreamId ?? 0;
    public int StreamCount => ActiveStreamIds.Count;
}

internal readonly record struct ReceiveIngressRouteDecision(
    ReceiveRouteProjectionDecision PacketDecision,
    IReadOnlyList<ReceiveRouteProjectionDecision> PrecedingDecisions)
{
    public ChannelRouteKey RouteKey => PacketDecision.RouteKey;
    public uint PrimaryStreamId => PacketDecision.PrimaryStreamId;
    public int StreamCount => PacketDecision.StreamCount;
    public ReceiveAction Actions => PacketDecision.Actions;
    public ReceiveStreamDecision StreamDecision => PacketDecision.StreamDecision;
    public ImmutableHashSet<uint> ActiveStreamIds => PacketDecision.ActiveStreamIds;
}

// The common packet path has one route and therefore does not allocate a
// collection. Destinationless terminators may close multiple tracked routes;
// those rare additional decisions remain private so the envelope is immutable
// to consumers.
internal readonly struct ReceiveIngressRoutingDecision
{
    private readonly ReceiveIngressRouteDecision primary;
    private readonly ReceiveIngressRouteDecision[]? additional;

    private ReceiveIngressRoutingDecision(
        ReceiveIngressRouteDecision primary,
        ReceiveIngressRouteDecision[]? additional)
    {
        this.primary = primary;
        this.additional = additional;
        HasDecision = true;
    }

    public static ReceiveIngressRoutingDecision Empty => default;
    public bool HasDecision { get; }
    public int Count => HasDecision ? 1 + (additional?.Length ?? 0) : 0;
    public bool IsContinuationOnly =>
        HasDecision &&
        additional is null &&
        primary.PrecedingDecisions.Count == 0 &&
        primary.StreamDecision.Transition == ReceiveStreamTransition.Continued;

    public bool TryGet(
        ChannelRouteKey routeKey,
        out ReceiveIngressRouteDecision decision)
    {
        if (HasDecision && primary.RouteKey == routeKey)
        {
            decision = primary;
            return true;
        }

        if (additional is not null)
        {
            for (int index = 0; index < additional.Length; index++)
            {
                if (additional[index].RouteKey == routeKey)
                {
                    decision = additional[index];
                    return true;
                }
            }
        }

        decision = default;
        return false;
    }

    public static ReceiveIngressRoutingDecision Create(
        ReceiveIngressRouteDecision primary,
        IReadOnlyList<ReceiveIngressRouteDecision>? additional = null)
        => new(
            primary,
            additional is null || additional.Count == 0
                ? null
                : additional.ToArray());
}
