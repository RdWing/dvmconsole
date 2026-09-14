// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelAuthorityStateTests
{
    [Fact]
    public void AuthorityIsOwnedWithoutAViewAndObserverFailureDoesNotHideItsChange()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 1, 0));
        var observed = new List<TargetAuthorityState>();
        channel.AuthorityChanged += (_, _) => throw new InvalidOperationException("Broken presentation observer");
        channel.AuthorityChanged += (_, _) => observed.Add(channel.Authority);
        Assert.Equal(TargetAuthorityState.Pending, channel.Authority);
        Assert.True(channel.SetAuthority(TargetAuthorityState.Unavailable));
        Assert.False(channel.SetAuthority(TargetAuthorityState.Unavailable));
        Assert.True(channel.SetAuthority(TargetAuthorityState.Available));
        Assert.Equal([TargetAuthorityState.Unavailable, TargetAuthorityState.Available], observed);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.SetAuthority((TargetAuthorityState)100));
        Assert.Equal(TargetAuthorityState.Available, channel.Authority);
    }
}
