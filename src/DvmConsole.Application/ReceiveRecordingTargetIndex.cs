// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Operations;

namespace DvmConsole.Application;

/// <summary>
/// Maintains recording ownership by logical receive route. The index is
/// rebuilt only when an operator changes TAR state; decoded PCM then resolves
/// its destination without walking the configured topology.
/// </summary>
internal sealed class ReceiveRecordingTargetIndex
{
    private readonly object sync = new();
    private readonly IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels;
    private Dictionary<ChannelRouteKey, ChannelId> armedTargets = [];

    public ReceiveRecordingTargetIndex(IEnumerable<ConsoleChannelState> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = channels.Distinct().ToDictionary(channel => channel.Id);
        Refresh();
    }

    public void Refresh()
    {
        var next = new Dictionary<ChannelRouteKey, ChannelId>();
        foreach (ConsoleChannelState channel in channels.Values)
        {
            if (channel.Operator.Snapshot.RecordingEnabled)
                next.TryAdd(channel.Identity.RouteKey, channel.Id);
        }

        lock (sync)
            armedTargets = next;
    }

    public ChannelId? Resolve(ChannelId decodedChannel)
    {
        ConsoleChannelState channel = channels[decodedChannel];
        if (channel.Operator.Snapshot.RecordingEnabled)
            return decodedChannel;

        lock (sync)
            return armedTargets.TryGetValue(channel.Identity.RouteKey, out ChannelId target)
                ? target : null;
    }
}
