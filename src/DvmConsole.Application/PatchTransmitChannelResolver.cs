using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

// Resolves persisted patch addresses to immutable application descriptors.
// The identity index is fixed for a session; operational fields are refreshed
// through the supplied ID lookup whenever a target is started.
internal sealed class PatchTransmitChannelResolver
{
    private readonly Dictionary<string, TransmitChannelDescriptor> channelsByIdentity;
    private readonly Dictionary<string, TransmitChannelDescriptor> unambiguousLegacyChannels;
    private readonly Func<ChannelId, TransmitChannelDescriptor?> resolveCurrent;

    public PatchTransmitChannelResolver(
        IEnumerable<TransmitChannelDescriptor> channels,
        Func<ChannelId, TransmitChannelDescriptor?>? resolveCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        TransmitChannelDescriptor[] configuredChannels = channels
            .GroupBy(channel => channel.Id)
            .Select(group => group.First())
            .ToArray();
        channelsByIdentity = configuredChannels
            .GroupBy(channel => FromChannel(channel).Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        unambiguousLegacyChannels = configuredChannels
            .GroupBy(
                channel => BuildLegacyKey(
                    channel.Definition.SystemName,
                    channel.Definition.DestinationId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                group.Key,
                DistinctMembers = group
                    .GroupBy(channel => FromChannel(channel).Key, StringComparer.OrdinalIgnoreCase)
                    .Select(memberCopies => memberCopies.First())
                    .ToArray()
            })
            .Where(group => group.DistinctMembers.Length == 1)
            .ToDictionary(group => group.Key, group => group.DistinctMembers[0], StringComparer.OrdinalIgnoreCase);
        this.resolveCurrent = resolveCurrent ?? (id => configuredChannels.FirstOrDefault(channel => channel.Id == id));
    }

    public static PatchMemberAddress FromChannel(TransmitChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new PatchMemberAddress(
            channel.Definition.SystemName,
            channel.Definition.DestinationId,
            channel.Definition.Name);
    }

    public TransmitChannelDescriptor? Resolve(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        TransmitChannelDescriptor? indexed = member.HasConfiguredChannelIdentity
            ? channelsByIdentity.GetValueOrDefault(member.Key)
            : unambiguousLegacyChannels.GetValueOrDefault(
                BuildLegacyKey(member.SystemName, member.DestinationId));
        return indexed is null ? null : resolveCurrent(indexed.Id) ?? indexed;
    }

    public TransmitChannelDescriptor? Resolve(ChannelId id)
        => resolveCurrent(id);

    private static string BuildLegacyKey(string systemName, uint destinationId)
        => $"{systemName.Trim().ToLowerInvariant()}|{destinationId}";
}
