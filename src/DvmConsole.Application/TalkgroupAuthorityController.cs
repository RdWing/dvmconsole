// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;

namespace DvmConsole.Application;

/// <summary>Applies radio authority to session-owned channels and identifies newly barred targets.</summary>
public sealed class TalkgroupAuthorityController
{
    private readonly FrozenDictionary<ChannelId, (ConsoleChannelState State, SystemId System)> channels;

    public TalkgroupAuthorityController(IEnumerable<ConsoleChannelState> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = channels.DistinctBy(channel => channel.Id).ToFrozenDictionary(channel => channel.Id,
            channel => (channel, SystemId.FromName(channel.Runtime.Definition.SystemName)));
    }

    public IReadOnlyList<ChannelId> Apply(TalkgroupAuthorityRecord record, Action<ChannelId>? changed = null)
    {
        List<ChannelId>? unavailable = null;
        foreach (TalkgroupAuthorityChannelRecord update in record.Channels)
        {
            if (!channels.TryGetValue(update.ChannelId, out var channel) || channel.System != record.SystemId)
                continue;
            if (!channel.State.SetAuthority(update.State)) continue;
            if (update.State == TargetAuthorityState.Unavailable)
                (unavailable ??= []).Add(update.ChannelId);
            changed?.Invoke(update.ChannelId);
        }
        return unavailable is null ? Array.Empty<ChannelId>() : unavailable;
    }
}
