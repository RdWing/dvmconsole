using System.Runtime.CompilerServices;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Keeps the public/test-facing ChannelViewModel traffic facade compatible
// without putting lifecycle ownership back into the view model. Production
// traffic supplies the shared route-runtime decision captured at ingress.
internal static class ChannelReceiveProjectionCompatibility
{
    private static readonly ConditionalWeakTable<ChannelViewModel, RuntimeHolder> runtimes = new();

    public static ReceiveRouteProjectionDecision Observe(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        DateTimeOffset now)
        => GetHolder(channel).Adapter.ObserveCompatibility(channel, traffic, now);

    public static ReceiveRouteProjectionDecision Advance(
        ChannelViewModel channel,
        DateTimeOffset now)
        => GetHolder(channel).Adapter.AdvanceCompatibility(channel, now);

    private static RuntimeHolder GetHolder(ChannelViewModel channel)
        => runtimes.GetValue(channel, static candidate => new RuntimeHolder(candidate));

    private sealed class RuntimeHolder
    {
        public RuntimeHolder(ChannelViewModel channel)
        {
            var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
            {
                [(FneTrafficProtocolMapper.FromChannelProtocol(
                    channel.SessionDefinition.Protocol),
                    channel.SessionDefinition.DestinationId)] = [channel]
            };
            Adapter = new ReceiveRoutePresentationAdapter(routes);
        }

        public ReceiveRoutePresentationAdapter Adapter { get; }
    }
}
