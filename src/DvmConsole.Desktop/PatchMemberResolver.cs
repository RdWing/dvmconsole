// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Resolves persisted and runtime patch identities to one configured channel.
// Legacy settings remain usable when system/talkgroup identifies exactly one
// channel; ambiguous legacy settings never choose a protocol arbitrarily.
internal sealed class PatchMemberResolver
{
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channels;
    private readonly PatchTransmitChannelResolver resolver;

    public PatchMemberResolver(IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ChannelViewModel[] configured = channels.ToArray();
        this.channels = configured.GroupBy(channel => channel.Id)
            .ToDictionary(group => group.Key, group => group.First());
        resolver = new PatchTransmitChannelResolver(configured.Select(channel => channel.ToTransmitDescriptor()));
    }

    public static PatchMemberAddress FromChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new PatchMemberAddress(
            channel.Definition.SystemName,
            channel.Definition.DestinationId,
            channel.Definition.Name);
    }

    public static PatchMemberAddress FromChannel(IPatchMemberChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new PatchMemberAddress(
            channel.SystemName,
            channel.DestinationId,
            channel.Name);
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
        return resolver.Resolve(member) is { } channel ? channels[channel.Id] : null;
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

}
