using System.Runtime.CompilerServices;
using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

// Preserves the established receive-routing facade while moving steady-state
// lookup and lifecycle decisions behind one immutable operations/presentation
// adapter per route table.
internal static class ReceiveAudioTrafficRouter
{
    private static readonly ConditionalWeakTable<object, ReceiveRoutePresentationAdapter>
        adapters = new();

    public static ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ReceiveRoutePresentationAdapter adapter = GetAdapter(routes);
        ReceiveIngressRoutingDecision ingressDecision = adapter.ObserveIngress(
            traffic,
            isTrackingStream);
        return adapter.ResolveTargets(
            decodeChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public static ReceiveDispatchTargets ResolveDispatchTargets(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        bool includeRecordingChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).ResolveDispatchTargets(
            decodeChannels,
            includeRecordingChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public static ReceiveIngressRoutingDecision ObserveIngress(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).ObserveIngress(traffic, isTrackingStream, observedAt);
    }

    public static ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).ResolveTargets(
            decodeChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public static ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ReceiveRoutePresentationAdapter adapter = GetAdapter(routes);
        return adapter.ResolvePresentationCandidates(
            systemChannels,
            traffic,
            ReceiveIngressRoutingDecision.Empty,
            isAudioActive,
            isPatchActive,
            isTrackingStream);
    }

    public static IReadOnlyList<ReceiveRouteProjectionDecision> Advance(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).Advance(now);
    }

    public static bool IsActive(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        ChannelRouteKey routeKey,
        uint streamId)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).IsActive(routeKey, streamId);
    }

    public static ChannelViewModel? ResolveProjectionTarget(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        ChannelRouteKey routeKey,
        uint streamId,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).ResolveProjectionTarget(
            routeKey,
            streamId,
            isAudioActive,
            isPatchActive);
    }

    public static ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return GetAdapter(routes).ResolvePresentationCandidates(
            systemChannels,
            traffic,
            ingressDecision,
            isAudioActive,
            isPatchActive,
            isTrackingStream);
    }

    private static ReceiveRoutePresentationAdapter GetAdapter(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes)
        => adapters.GetValue(
            routes,
            static key => new ReceiveRoutePresentationAdapter(
                (IReadOnlyDictionary<
                    (FneTrafficProtocol Protocol, uint DestinationId),
                    ChannelViewModel[]>)key));
}
