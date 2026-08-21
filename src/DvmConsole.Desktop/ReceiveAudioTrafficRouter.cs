using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Selects only the already-open receive sessions that should get a network
// frame on the latency-sensitive path. UI lifecycle and history processing
// still observe the same frame independently.
internal static class ReceiveAudioTrafficRouter
{
    public static ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> activeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(activeChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolveTerminatorTargets(
                routes,
                activeChannels,
                traffic,
                isTrackingStream);
        }

        if (!ReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            !ReceiveTrafficClassifier.IsDefinitiveStart(traffic) &&
            !ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            return [];
        }

        if (!routes.TryGetValue(
                (traffic.Protocol, traffic.DestinationId),
                out ChannelViewModel[]? candidates))
        {
            return [];
        }

        int targetCount = 0;
        for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            if (IsEligibleVoiceTarget(
                    candidates,
                    candidateIndex,
                    activeChannels,
                    traffic))
            {
                targetCount++;
            }
        }

        if (targetCount == 0)
            return [];

        var targets = new ChannelViewModel[targetCount];
        int targetIndex = 0;
        for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            if (IsEligibleVoiceTarget(
                    candidates,
                    candidateIndex,
                    activeChannels,
                    traffic))
            {
                targets[targetIndex++] = candidates[candidateIndex];
            }
        }
        return targets;
    }

    private static ChannelViewModel[] ResolveTerminatorTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> activeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        int targetCount = 0;
        for (int index = 0; index < activeChannels.Count; index++)
        {
            if (IsEligibleTerminatorTarget(
                    routes,
                    activeChannels[index],
                    traffic,
                    isTrackingStream))
            {
                targetCount++;
            }
        }

        if (targetCount == 0)
            return [];

        var targets = new ChannelViewModel[targetCount];
        int targetIndex = 0;
        for (int index = 0; index < activeChannels.Count; index++)
        {
            ChannelViewModel channel = activeChannels[index];
            if (IsEligibleTerminatorTarget(routes, channel, traffic, isTrackingStream))
                targets[targetIndex++] = channel;
        }
        return targets;
    }

    private static bool IsEligibleVoiceTarget(
        ChannelViewModel[] candidates,
        int candidateIndex,
        IReadOnlyList<ChannelViewModel> activeChannels,
        FneTrafficFrame traffic)
    {
        ChannelViewModel candidate = candidates[candidateIndex];
        if (!ContainsReference(activeChannels, candidate) ||
            !MatchesProtocolAndSlot(candidate, traffic))
        {
            return false;
        }

        for (int priorIndex = 0; priorIndex < candidateIndex; priorIndex++)
        {
            ChannelViewModel prior = candidates[priorIndex];
            if (ContainsReference(activeChannels, prior) &&
                MatchesProtocolAndSlot(prior, traffic) &&
                HasEquivalentReceiveIdentity(prior, candidate))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsEligibleTerminatorTarget(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
        => ContainsRoutedChannel(routes, channel) &&
           MatchesProtocolAndSlot(channel, traffic) &&
           isTrackingStream(channel, traffic.StreamId);

    private static bool ContainsRoutedChannel(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        ChannelViewModel channel)
    {
        foreach (ChannelViewModel[] routedChannels in routes.Values)
        {
            if (ContainsReference(routedChannels, channel))
                return true;
        }
        return false;
    }

    private static bool ContainsReference(
        IReadOnlyList<ChannelViewModel> channels,
        ChannelViewModel target)
    {
        for (int index = 0; index < channels.Count; index++)
        {
            if (ReferenceEquals(channels[index], target))
                return true;
        }
        return false;
    }

    private static bool HasEquivalentReceiveIdentity(
        ChannelViewModel left,
        ChannelViewModel right)
        => left.Definition.Mode == right.Definition.Mode &&
           left.Definition.DestinationId == right.Definition.DestinationId &&
           (left.Definition.Mode != "dmr" || left.Definition.Slot == right.Definition.Slot);

    private static bool MatchesProtocolAndSlot(
        ChannelViewModel channel,
        FneTrafficFrame traffic)
    {
        FneTrafficProtocol protocol = channel.Definition.Mode switch
        {
            "dmr" => FneTrafficProtocol.Dmr,
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            "analog" => FneTrafficProtocol.Analog,
            _ => throw new InvalidOperationException(
                $"Unsupported channel mode '{channel.Definition.Mode}'.")
        };
        return protocol == traffic.Protocol &&
               (traffic.Protocol != FneTrafficProtocol.Dmr ||
                traffic.Slot == channel.Definition.Slot);
    }
}
