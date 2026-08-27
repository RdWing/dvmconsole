using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop.Tests;

// Gives each xUnit test instance explicit ownership of route lifecycle state
// while retaining the concise call shape used by the routing specifications.
internal sealed class ReceiveAudioTrafficRouterTestHarness
{
    private readonly Dictionary<object, DvmConsole.Desktop.ReceiveAudioTrafficRouter> routers =
        new(ReferenceEqualityComparer.Instance);

    public ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => Get(routes).ResolveTargets(decodeChannels, traffic, isTrackingStream);

    public ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => Get(routes).ResolveTargets(
            decodeChannels,
            traffic,
            ingressDecision,
            isTrackingStream);

    public ReceiveDispatchTargets ResolveDispatchTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        bool includeRecordingChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => Get(routes).ResolveDispatchTargets(
            decodeChannels,
            includeRecordingChannels,
            traffic,
            ingressDecision,
            isTrackingStream);

    public ReceiveIngressRoutingDecision ObserveIngress(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream,
        DateTimeOffset? observedAt = null)
        => Get(routes).ObserveIngress(traffic, isTrackingStream, observedAt);

    public ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => Get(routes).ResolvePresentationCandidates(
            systemChannels,
            traffic,
            isAudioActive,
            isPatchActive,
            isTrackingStream);

    public ChannelViewModel[] ResolvePresentationCandidates(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic,
        ReceiveIngressRoutingDecision ingressDecision,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => Get(routes).ResolvePresentationCandidates(
            systemChannels,
            traffic,
            ingressDecision,
            isAudioActive,
            isPatchActive,
            isTrackingStream);

    public IReadOnlyList<ReceiveRouteProjectionDecision> Advance(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        DateTimeOffset now)
        => Get(routes).Advance(now);

    private DvmConsole.Desktop.ReceiveAudioTrafficRouter Get(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes)
    {
        if (!routers.TryGetValue(routes, out DvmConsole.Desktop.ReceiveAudioTrafficRouter? router))
        {
            router = new DvmConsole.Desktop.ReceiveAudioTrafficRouter(routes);
            routers.Add(routes, router);
        }
        return router;
    }
}
