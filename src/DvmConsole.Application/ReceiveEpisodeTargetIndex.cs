// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Resolves logical-call completion against application-owned channel state.</summary>
internal sealed class ReceiveEpisodeTargetIndex(IReadOnlyList<ReceiveIngressSystem> systems)
{
    public bool IsPhysicallyActive(ReceiveCallEpisodeSnapshot episode)
        => IsPhysicallyActive(systems, episode, static system => system.Name,
            static system => system.Channels, static channel => channel);

    public ReceiveEpisodeTargets Resolve(ReceiveCallEpisodeSnapshot episode)
    {
        ConsoleChannelState[] channels = systems
            .FirstOrDefault(system => system.Name.Equals(episode.SystemName, StringComparison.OrdinalIgnoreCase))
            ?.Channels.Where(channel => ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Runtime.Definition.Protocol) == episode.Protocol &&
                channel.Runtime.Definition.DestinationId == episode.DestinationId &&
                (episode.Protocol != RadioMediaProtocol.Dmr || channel.Runtime.Definition.Slot == episode.Slot))
            .Distinct().ToArray() ?? [];
        return new(channels.FirstOrDefault()?.Identity.Name ?? episode.DestinationId.ToString(CultureInfo.InvariantCulture),
            channels.Select(channel => channel.Id).ToArray());
    }

    // Static selectors let transitional host adapters borrow their existing
    // lists without allocating per sweep or retaining presentation in Application.
    public static bool IsPhysicallyActive<TSystem, TChannel>(
        IReadOnlyList<TSystem> systems, ReceiveCallEpisodeSnapshot episode,
        Func<TSystem, string> name, Func<TSystem, IReadOnlyList<TChannel>> channels,
        Func<TChannel, ConsoleChannelState> state)
    {
        for (int systemIndex = 0; systemIndex < systems.Count; systemIndex++)
        {
            TSystem system = systems[systemIndex];
            if (!name(system).Equals(episode.SystemName, StringComparison.OrdinalIgnoreCase)) continue;
            IReadOnlyList<TChannel> systemChannels = channels(system);
            for (int channelIndex = 0; channelIndex < systemChannels.Count; channelIndex++)
            {
                ChannelReceiveState receive = state(systemChannels[channelIndex]).Receive;
                for (int streamIndex = 0; streamIndex < episode.StreamIds.Count; streamIndex++)
                    if (receive.IsTracking(episode.StreamIds[streamIndex])) return true;
            }
            return false;
        }
        return false;
    }
}
