using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Selects the receive decode sessions that should get a network frame on the
// latency-sensitive path. Callers may include TAR-armed sessions that are
// still opening so their ordered worker can retain the start of the call.
internal static class ReceiveAudioTrafficRouter
{
    public static ChannelViewModel[] ResolveTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(decodeChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return ResolveTerminatorTargets(
                routes,
                decodeChannels,
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
                    decodeChannels,
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
                    decodeChannels,
                    traffic))
            {
                targets[targetIndex++] = candidates[candidateIndex];
            }
        }
        return targets;
    }

    private static ChannelViewModel[] ResolveTerminatorTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        int targetCount = 0;
        for (int index = 0; index < decodeChannels.Count; index++)
        {
            if (IsEligibleTerminatorTarget(
                    routes,
                    decodeChannels[index],
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
        for (int index = 0; index < decodeChannels.Count; index++)
        {
            ChannelViewModel channel = decodeChannels[index];
            if (IsEligibleTerminatorTarget(routes, channel, traffic, isTrackingStream))
                targets[targetIndex++] = channel;
        }
        return targets;
    }

    private static bool IsEligibleVoiceTarget(
        ChannelViewModel[] candidates,
        int candidateIndex,
        IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic)
    {
        ChannelViewModel candidate = candidates[candidateIndex];
        if (!ContainsReference(decodeChannels, candidate) ||
            !MatchesProtocolAndSlot(candidate, traffic))
        {
            return false;
        }

        for (int priorIndex = 0; priorIndex < candidateIndex; priorIndex++)
        {
            ChannelViewModel prior = candidates[priorIndex];
            if (ContainsReference(decodeChannels, prior) &&
                MatchesProtocolAndSlot(prior, traffic) &&
                ChannelReceiveIdentity.AreEquivalent(prior, candidate))
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
