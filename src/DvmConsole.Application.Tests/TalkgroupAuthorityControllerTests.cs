// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class TalkgroupAuthorityControllerTests
{
    [Fact]
    public void AuthorityIsSystemScopedAndOnlyNewlyUnavailableChannelsRequireStopping()
    {
        ConsoleChannelState Channel(string system) => new(ChannelRuntimeDefinition.FromConfiguration(
            new ChannelConfiguration { Name = "Operations", System = system, Mode = "p25", Tgid = "100" }));
        var first = Channel("First");
        var second = Channel("Second");
        var controller = new TalkgroupAuthorityController([first, second]);
        var changes = new List<ChannelId>();
        var record = new TalkgroupAuthorityRecord(SystemId.FromName("First"), [
            new(first.Id, TargetAuthorityState.Unavailable, null),
            new(first.Id, TargetAuthorityState.Unavailable, null),
            new(second.Id, TargetAuthorityState.Unavailable, null)], DateTimeOffset.UnixEpoch);
        var secondInitial = second.Authority;

        Assert.Equal([first.Id], controller.Apply(record, changes.Add));
        Assert.Equal([first.Id], changes);
        Assert.Equal(secondInitial, second.Authority);
        Assert.Empty(controller.Apply(record, changes.Add));
        Assert.Single(changes);
        Assert.Empty(controller.Apply(record with { Channels = [new(first.Id, TargetAuthorityState.Available, null)] }, changes.Add));
        Assert.Equal(TargetAuthorityState.Available, first.Authority);
        Assert.Equal(2, changes.Count);
    }
}
