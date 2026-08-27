using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

// Explicitly owns the mutable receive-route lifecycle for one immutable route
// table. Callers keep this object for as long as the configured routes live.
internal sealed class ReceiveAudioTrafficRouter
{
    private readonly ReceiveRoutePresentationAdapter adapter;

    public ReceiveAudioTrafficRouter(
        IReadOnlyDictionary<
            (FneTrafficProtocol Protocol, uint DestinationId),
            ChannelViewModel[]> routes)
        => adapter = new ReceiveRoutePresentationAdapter(routes);

    public ChannelViewModel[] ResolveTargets(
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ReceiveIngressRoutingDecision ingressDecision = adapter.ObserveIngress(
            traffic,
            isTrackingStream);
        return adapter.ResolveTargets(
            decodeChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public ReceiveDispatchTargets ResolveDispatchTargets(
        IReadOnlyList<ChannelViewModel> decodeChannels,
        bool includeRecordingChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        return adapter.ResolveDispatchTargets(
            decodeChannels,
            includeRecordingChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public ReceiveIngressRoutingDecision ObserveIngress(
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt = null)
    {
        return adapter.ObserveIngress(traffic, isTrackingStream, observedAt);
    }

    public ChannelViewModel[] ResolveTargets(
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        return adapter.ResolveTargets(
            decodeChannels,
            traffic,
            ingressDecision,
            isTrackingStream);
    }

    public ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        return adapter.ResolvePresentationCandidates(
            systemChannels,
            traffic,
            ReceiveIngressRoutingDecision.Empty,
            isAudioActive,
            isPatchActive,
            isTrackingStream);
    }

    public IReadOnlyList<ReceiveRouteProjectionDecision> Advance(DateTimeOffset now)
        => adapter.Advance(now);

    public bool IsActive(
        ChannelRouteKey routeKey,
        uint streamId)
        => adapter.IsActive(routeKey, streamId);

    public ChannelViewModel? ResolveProjectionTarget(
        ChannelRouteKey routeKey,
        uint streamId,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
    {
        return adapter.ResolveProjectionTarget(
            routeKey,
            streamId,
            isAudioActive,
            isPatchActive);
    }

    public ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        return adapter.ResolvePresentationCandidates(
            systemChannels,
            traffic,
            ingressDecision,
            isAudioActive,
            isPatchActive,
            isTrackingStream);
    }

}
