// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed record ReceiveTrafficPresentationPorts(
    IReceiveIngressPresentation Ingress,
    IReceiveMediaPresentation Media,
    IReceiveCallHistoryPresentation History,
    IReceiveChannelTrafficPort Channels);

/// <summary>One shared ingress, media dispatch and call-history graph for a live session.</summary>
internal sealed class ReceiveTrafficRuntime : IReceiveIngressProjection
{
    public ReceiveIngressCoordinator Ingress { get; }
    private readonly object synchronization;
    public ReceiveChannelTrafficCoordinator Channels { get; }

    public ReceiveTrafficRuntime(IReadOnlyList<ReceiveIngressSystem> systems,
        ReceiveCallEpisodeTracker episodes, ReceiveMediaPort media,
        ReceiveEpisodeRetirement retirement, ConsoleCallHistory history,
        ConsoleChannelMediaDirectory channels, ConsoleSessionAdmission admission, ReceiveBufferingRuntime buffering,
        object synchronization,
        IRadioReceiveFrameNormalizer normalizer, ReceiveTrafficPresentationPorts presentation,
        ConsoleSessionState? sharedState = null)
    {
        this.synchronization = synchronization;
        var dispatch = new ReceiveMediaDispatchCoordinator(media, channels, admission, presentation.Media);
        Ingress = new ReceiveIngressCoordinator(systems, episodes, media, dispatch,
            presentation.Ingress, this, normalizer, admission, buffering, sharedState: sharedState);
        var callHistory = new ReceiveCallHistoryCoordinator(history, channels, presentation.History);
        Channels = new ReceiveChannelTrafficCoordinator(Ingress, media, dispatch, retirement, callHistory, presentation.Channels);
    }

    void IReceiveIngressProjection.Apply(ReceiveIngressSystem system, ReceiveIngressWorkItem workItem)
    {
        lock (synchronization)
            Channels.Process(system, workItem);
    }

    void IReceiveIngressProjection.Advance(ConsoleChannelState channel,
        ReceiveRouteProjectionDecision decision, DateTimeOffset now)
    {
        lock (synchronization)
            Channels.ProjectReceiveLifecycleDecision(channel, decision, now);
    }
}
