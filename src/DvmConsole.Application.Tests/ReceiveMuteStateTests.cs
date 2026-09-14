// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceiveMuteStateTests
{
    [Fact]
    public void SameNamedZonesHaveIndependentMembershipAndIntent()
    {
        ChannelId first = Id("First");
        ChannelId second = Id("Second");
        var firstZone = new ReceiveMuteScope(ReceiveMuteScopeKind.Zone, "Dispatch", [first]);
        var secondZone = new ReceiveMuteScope(ReceiveMuteScopeKind.Zone, "Dispatch", [second]);
        var state = new ReceiveMuteState();

        state.Toggle(firstZone);
        Assert.True(state.IsMuted(first));
        Assert.False(state.IsMuted(second));
        Assert.False(state.IsMuted(secondZone));
        state.Toggle(secondZone);
        state.Toggle(firstZone);
        Assert.False(state.IsMuted(first));
        Assert.True(state.IsMuted(second));
    }

    [Fact]
    public void ScopeMembershipIsFrozenAndSystemReasonTakesPrecedence()
    {
        ChannelId channel = Id("System");
        var members = new List<ChannelId> { channel };
        var zone = new ReceiveMuteScope(ReceiveMuteScopeKind.Zone, "Dispatch", members);
        var system = new ReceiveMuteScope(ReceiveMuteScopeKind.System, "System", members);
        members.Clear();
        var state = new ReceiveMuteState();
        state.Toggle(zone);
        state.Toggle(system);

        Assert.Equal("global output mute", state.GetEffectiveReason(channel, true));
        Assert.Equal("system System output mute", state.GetEffectiveReason(channel, false));
        state.Toggle(system);
        Assert.Equal("zone Dispatch output mute", state.GetEffectiveReason(channel, false));
        Assert.False(state.ShouldEnableLivePlayback(channel, selected: true, suspended: false));
        state.Toggle(zone);
        Assert.False(state.ShouldEnableLivePlayback(channel, selected: false, suspended: false));
        Assert.False(state.ShouldEnableLivePlayback(channel, selected: true, suspended: true));
        Assert.True(state.ShouldEnableLivePlayback(channel, selected: true, suspended: false));
    }

    private static ChannelId Id(string system) => new(new ChannelSessionId(system, ChannelProtocol.Dmr, 100, 0, "Dispatch"));
}
