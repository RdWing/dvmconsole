// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Operations;

namespace DvmConsole.Desktop;

/// <summary>
/// Maintains recording ownership by logical receive route. The index is
/// rebuilt only when an operator changes TAR state; decoded PCM then resolves
/// its destination without walking the configured topology.
/// </summary>
internal sealed class ReceiveRecordingTargetIndex
{
    private readonly object sync = new();
    private readonly ChannelViewModel[] channels;
    private Dictionary<ChannelRouteKey, ChannelViewModel> armedTargets = [];

    public ReceiveRecordingTargetIndex(IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = channels.Distinct().ToArray();
        Refresh();
    }

    public void Refresh()
    {
        var next = new Dictionary<ChannelRouteKey, ChannelViewModel>();
        foreach (ChannelViewModel channel in channels)
        {
            if (channel.IsRecordingEnabled)
                next.TryAdd(channel.SessionDefinition.RouteKey, channel);
        }

        lock (sync)
            armedTargets = next;
    }

    public ChannelViewModel? Resolve(ChannelViewModel decodedChannel)
    {
        ArgumentNullException.ThrowIfNull(decodedChannel);
        if (decodedChannel.IsRecordingEnabled)
            return decodedChannel;

        lock (sync)
            return armedTargets.GetValueOrDefault(decodedChannel.SessionDefinition.RouteKey);
    }
}
