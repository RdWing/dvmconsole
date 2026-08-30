using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task AppliesTheFneProtocolSpecificTalkgroupRulesWithoutChangingConfiguration()
    {
        ChannelViewModel dmr = Channel("DMR", "748", "dmr", slot: 2);
        ChannelViewModel p25 = Channel("P25", "748", "p25");
        ChannelViewModel nxdn = Channel("NXDN", "999", "nxdn");
        ChannelViewModel analog = Channel("Analog", "747", "analog");
        await using var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031",
            [dmr, p25, nxdn, analog]);
        FneTalkgroupAuthority authority = FneTalkgroupAuthority.FromRules(
        [
            new FneTalkgroupRule(748, 1, true, false, false),
            new FneTalkgroupRule(747, 2, true, false, false)
        ]);

        IReadOnlyList<ChannelViewModel> newlyUnavailable =
            system.ApplyTalkgroupAuthority(authority);

        Assert.Equal([dmr, nxdn], newlyUnavailable);
        Assert.Equal(FneTalkgroupAvailability.Unavailable, dmr.TalkgroupAvailability);
        Assert.Equal(FneTalkgroupAvailability.Available, p25.TalkgroupAvailability);
        Assert.Equal(FneTalkgroupAvailability.Unavailable, nxdn.TalkgroupAvailability);
        Assert.Equal(FneTalkgroupAvailability.Available, analog.TalkgroupAvailability);
        Assert.Equal((byte)1, dmr.Definition.Slot);
        Assert.Equal((uint)748, dmr.Definition.DestinationId);

        Assert.Empty(system.ApplyTalkgroupAuthority(authority));
        Assert.Empty(system.ApplyTalkgroupAuthority(FneTalkgroupAuthority.Pending));
        Assert.All(
            system.Channels,
            channel => Assert.Equal(FneTalkgroupAvailability.Pending, channel.TalkgroupAvailability));
    }

    private static ChannelViewModel Channel(
        string name,
        string talkgroup,
        string mode,
        int slot = 1)
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "Test",
            Tgid = talkgroup,
            Mode = mode,
            Slot = slot
        });
}
