// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Application;
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

        ChannelId selected = Assert.Single(
            PatchSourceSelectionPolicy.SelectEnabledSources([Snapshot(group)]));

        Assert.Equal(((ChannelViewModel)beta.Channel).Id, selected);
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

        ChannelId[] selected = PatchSourceSelectionPolicy.SelectEnabledSources(
            [Snapshot(patch), Snapshot(multiSelect)]);

        Assert.Equal([((ChannelViewModel)alpha.Channel).Id, ((ChannelViewModel)beta.Channel).Id], selected);
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

    private static ConsoleGroupDefinitionSnapshot Snapshot(PatchGroupEditorViewModel group)
        => new(group.Name, group.IsMultiSelect,
            [.. group.GetMembersInRoutingOrder().Select(member => ((ChannelViewModel)member.Channel).Id)],
            group.IsEnabled, group.IsOneWay, 0);

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
