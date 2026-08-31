using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchSourceSelectionPolicyTests
{
    [Fact]
    public void SelectsOnlyTheExplicitOneWaySource()
    {
        PatchMemberEditorViewModel alpha = Member("Alpha", 100, selected: true);
        PatchMemberEditorViewModel beta = Member("Beta", 200, selected: true);
        var group = new PatchGroupEditorViewModel(
            "Dispatch",
            enabled: true,
            oneWay: true,
            [alpha, beta]);
        group.SelectedSource = beta;

        ChannelViewModel selected = Assert.Single(
            PatchSourceSelectionPolicy.SelectEnabledSources([group]));

        Assert.Same(beta.Channel, selected);
    }

    [Fact]
    public void SelectsAllTwoWaySourcesAndExcludesMultiSelectGroups()
    {
        PatchMemberEditorViewModel alpha = Member("Alpha", 100, selected: true);
        PatchMemberEditorViewModel beta = Member("Beta", 200, selected: true);
        var patch = new PatchGroupEditorViewModel(
            "Dispatch",
            enabled: true,
            oneWay: false,
            [alpha, beta]);
        var multiSelect = new PatchGroupEditorViewModel(
            "Operations",
            enabled: true,
            oneWay: false,
            [Member("Gamma", 300, selected: true)],
            isMultiSelect: true);

        ChannelViewModel[] selected = PatchSourceSelectionPolicy.SelectEnabledSources(
            [patch, multiSelect]);

        Assert.Equal([alpha.Channel, beta.Channel], selected);
    }

    [Fact]
    public void ReceiveOnlyMemberCanBeTheOneWaySourceButNotADestination()
    {
        PatchMemberEditorViewModel receiveOnly = Member(
            "Receiver",
            100,
            selected: true,
            receiveOnly: true);
        PatchMemberEditorViewModel destination = Member("Transmitter", 200, selected: true);
        var group = new PatchGroupEditorViewModel(
            "Dispatch",
            enabled: true,
            oneWay: true,
            [receiveOnly, destination]);

        Assert.True(receiveOnly.CanReceive);
        Assert.False(receiveOnly.CanTransmit);
        Assert.True(receiveOnly.IsSelectionEnabled);
        Assert.Same(receiveOnly, group.SelectedSource);
        Assert.Null(group.GetMembershipValidationError());
        Assert.Same(receiveOnly, group.GetMembersInRoutingOrder()[0]);

        group.SelectedSource = destination;

        Assert.Contains("destinations must be transmit-capable", group.GetMembershipValidationError());

        group.IsOneWay = false;

        Assert.False(receiveOnly.IsSelectionEnabled);
        Assert.Contains("members cannot transmit", group.GetMembershipValidationError());
    }

    private static PatchMemberEditorViewModel Member(
        string systemName,
        uint destinationId,
        bool selected,
        bool receiveOnly = false)
        => new(
            new ChannelViewModel(new ChannelConfiguration
            {
                Name = $"{systemName} Dispatch",
                System = systemName,
                Tgid = destinationId.ToString(),
                Mode = "p25",
                RxOnly = receiveOnly
            }),
            selected);
}
