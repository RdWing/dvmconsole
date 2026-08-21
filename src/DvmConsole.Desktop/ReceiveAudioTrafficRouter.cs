using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Selects only the already-open receive sessions that should get a network
// frame on the latency-sensitive path. UI lifecycle and history processing
// still observe the same frame independently.
internal static class ReceiveAudioTrafficRouter
{
    public static IReadOnlyList<ChannelViewModel> ResolveTargets(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> routes,
        IReadOnlyList<ChannelViewModel> activeChannels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> isTrackingStream)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(activeChannels);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(isTrackingStream);

        HashSet<ChannelViewModel> systemChannels = routes.Values
            .SelectMany(channels => channels)
            .ToHashSet();

        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            return activeChannels
                .Where(systemChannels.Contains)
                .Where(channel => MatchesProtocolAndSlot(channel, traffic))
                .Where(channel => isTrackingStream(channel, traffic.StreamId))
                .ToArray();
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

        HashSet<ChannelViewModel> active = activeChannels.ToHashSet();
        return candidates
            .Where(active.Contains)
            .Where(channel => MatchesProtocolAndSlot(channel, traffic))
            .GroupBy(channel => (
                channel.Definition.Mode,
                channel.Definition.DestinationId,
                Slot: channel.Definition.Mode == "dmr" ? channel.Definition.Slot : (byte)0))
            .Select(group => group.First())
            .ToArray();
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
