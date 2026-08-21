using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveAudioTrafficRouterTests
{
    [Fact]
    public void RoutesOnlyTheActiveMatchingSlotAndDeduplicatesZoneCopies()
    {
        ChannelViewModel slotOne = Channel("Slot 1", "100", slot: 1);
        ChannelViewModel duplicate = Channel("Slot 1 Copy", "100", slot: 1);
        ChannelViewModel slotTwo = Channel("Slot 2", "100", slot: 2);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [slotOne, duplicate, slotTwo]
        };

        IReadOnlyList<ChannelViewModel> targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [slotOne, duplicate, slotTwo],
            Traffic(slot: 0),
            (_, _) => false);

        Assert.Single(targets);
        Assert.Same(slotOne, targets[0]);
    }

    [Fact]
    public void RoutesDestinationlessTerminatorOnlyToItsTrackedStream()
    {
        ChannelViewModel first = Channel("First", "100", slot: 1, mode: "p25");
        ChannelViewModel second = Channel("Second", "101", slot: 1, mode: "p25");
        ChannelViewModel dmrCollision = Channel("DMR", "102", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.P25, 100)] = [first],
            [(FneTrafficProtocol.P25, 101)] = [second],
            [(FneTrafficProtocol.Dmr, 102)] = [dmrCollision]
        };
        FneTrafficFrame terminator = new(
            FneTrafficProtocol.P25,
            1,
            0,
            0,
            null,
            "GROUP",
            "TERMINATOR",
            "TDU",
            2,
            77,
            []);

        IReadOnlyList<ChannelViewModel> targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [first, second, dmrCollision],
            terminator,
            (channel, streamId) => ReferenceEquals(channel, second) && streamId == 77);

        Assert.Single(targets);
        Assert.Same(second, targets[0]);
    }

    private static ChannelViewModel Channel(
        string name,
        string tgid,
        byte slot,
        string mode = "dmr")
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "System 1",
            Tgid = tgid,
            Mode = mode,
            Slot = slot
        });

    private static FneTrafficFrame Traffic(byte slot)
        => new(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            slot,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            77,
            []);
}
