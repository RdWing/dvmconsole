// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

// Maps desktop scope objects to portable identities. Application owns mute
// intent and policy; system-specific zone copies retain distinct memberships.
internal sealed class ReceiveOutputMutePolicy(ReceiveMuteState? sessionState = null)
{
    private readonly ReceiveMuteState state = sessionState ?? new();
    internal ReceiveMuteState State => state;
    private readonly ConditionalWeakTable<SystemViewModel, ReceiveMuteScope> systems = new();
    private readonly ConditionalWeakTable<ZoneViewModel, ReceiveMuteScope> zones = new();

    public bool IsMuted(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return state.IsMuted(channel.Id);
    }
    public bool IsMuted(SystemViewModel system) => state.IsMuted(Scope(system));
    public bool IsMuted(ZoneViewModel zone) => state.IsMuted(Scope(zone));
    public bool Toggle(SystemViewModel system) => state.Toggle(Scope(system));
    public bool Toggle(ZoneViewModel zone) => state.Toggle(Scope(zone));

    public string? GetEffectiveReason(ChannelViewModel channel, bool globallyMuted)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return state.GetEffectiveReason(channel.Id, globallyMuted);
    }

    public bool ShouldEnableLivePlayback(ChannelViewModel channel, bool isTemporarilySuspended)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return state.ShouldEnableLivePlayback(channel.Id, channel.OperatorState.Snapshot.AudioEnabled,
            isTemporarilySuspended);
    }

    private ReceiveMuteScope Scope(SystemViewModel system)
        => systems.GetValue(system, static value => new(ReceiveMuteScopeKind.System,
            value.Name, value.Channels.Select(channel => channel.Id)));

    private ReceiveMuteScope Scope(ZoneViewModel zone)
        => zones.GetValue(zone, static value => new(ReceiveMuteScopeKind.Zone,
            value.Name, value.Channels.Select(channel => channel.Id)));
}
