using DvmConsole.Application;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

// Desktop presentation adapter: converts at the edge so the patch service
// itself remains independent of view models.
internal static class DesktopPatchForwardingAdapter
{
    public static void ObserveTraffic(
        this PatchForwardingCoordinator coordinator,
        ChannelViewModel source,
        DvmConsole.FneClient.FneTrafficFrame traffic)
        => coordinator.ObserveTraffic(new ChannelId(source.SessionId), traffic);

    public static void ObserveDecodedSamples(
        this PatchForwardingCoordinator coordinator,
        ChannelViewModel source,
        ReadOnlyMemory<short> samples)
        => coordinator.ObserveDecodedSamples(
            new ChannelId(source.SessionId),
            source.StreamId ?? 0,
            source.SourceId ?? 0,
            samples);

    public static void ObserveDecodedSamples(
        this PatchForwardingCoordinator coordinator,
        ChannelViewModel source,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
        => coordinator.ObserveDecodedSamples(new ChannelId(source.SessionId), streamId, sourceId, samples);

    public static void StopSource(
        this PatchForwardingCoordinator coordinator,
        ChannelViewModel source,
        uint streamId)
        => coordinator.StopSource(new ChannelId(source.SessionId), streamId);

    public static int StopUnavailableTargets(
        this PatchForwardingCoordinator coordinator,
        IReadOnlyCollection<ChannelViewModel> channels)
        => coordinator.StopUnavailableTargets(
            channels.Select(channel => new ChannelId(channel.SessionId)).ToArray());
}
