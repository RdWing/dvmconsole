using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

// Resolves persisted and runtime patch identities to one configured channel.
// Legacy settings remain usable when system/talkgroup identifies exactly one
// channel; ambiguous legacy settings never choose a protocol arbitrarily.
internal sealed class PatchMemberResolver
{
    private readonly Dictionary<string, ChannelViewModel> channelsByIdentity;
    private readonly Dictionary<string, ChannelViewModel> unambiguousLegacyChannels;

    public PatchMemberResolver(IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ChannelViewModel[] configuredChannels = channels.ToArray();
        channelsByIdentity = configuredChannels
            .GroupBy(channel => FromChannel(channel).Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
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
            .ToDictionary(
                group => group.Key,
                group => group.DistinctMembers[0],
                StringComparer.OrdinalIgnoreCase);
    }

    public static PatchMemberAddress FromChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new PatchMemberAddress(
            channel.Definition.SystemName,
            channel.Definition.DestinationId,
            channel.Definition.Name);
    }

    public static PatchMemberSetting ToSetting(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new PatchMemberSetting
        {
            SystemName = member.SystemName,
            DestinationId = member.DestinationId,
            ChannelName = member.ChannelName
        };
    }

    public ChannelViewModel? Resolve(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.HasConfiguredChannelIdentity
            ? channelsByIdentity.GetValueOrDefault(member.Key)
            : unambiguousLegacyChannels.GetValueOrDefault(
                BuildLegacyKey(member.SystemName, member.DestinationId));
    }

    public ChannelViewModel? Resolve(PatchMemberSetting member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (string.IsNullOrWhiteSpace(member.SystemName) || member.DestinationId == 0)
            return null;

        return Resolve(new PatchMemberAddress(
            member.SystemName,
            member.DestinationId,
            member.ChannelName));
    }

    private static string BuildLegacyKey(string systemName, uint destinationId)
        => $"{systemName.Trim().ToLowerInvariant()}|{destinationId}";
}
