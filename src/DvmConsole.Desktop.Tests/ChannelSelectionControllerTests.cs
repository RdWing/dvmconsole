// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelSelectionControllerTests
{
    [Fact]
    public void RestoresPersistedChannelAndOwningSystem()
    {
        (SystemViewModel first, _) = CreateSystem("First", "Dispatch");
        (SystemViewModel second, ChannelViewModel selected) = CreateSystem("Second", "Tac");
        var settings = new UserSettings
        {
            RestoreSelectedChannelsOnStartup = true,
            LastSelectedSystemName = first.Name,
            LastSelectedChannelKey = selected.SettingsKey
        };
        var controller = new ChannelSelectionController([first, second], settings);

        controller.Restore();

        Assert.Same(first, controller.SelectedSystem);
        Assert.Same(selected, controller.SelectedChannel);
        Assert.True(first.IsSelected);
        Assert.False(second.IsSelected);
    }

    [Fact]
    public void PressedPttPreventsChangingAnExistingChannelSelection()
    {
        (SystemViewModel system, ChannelViewModel first) = CreateSystem("System", "Dispatch");
        var second = new ChannelViewModel(Channel("System", "Tac", "101"));
        var settings = new UserSettings();
        var controller = new ChannelSelectionController(
            [CreateSystem(system, [first, second])],
            settings);
        controller.Restore();
        Assert.True(controller.SelectChannel(first, selectionLocked: false));

        Assert.False(controller.SelectChannel(second, selectionLocked: true));

        Assert.Same(first, controller.SelectedChannel);
    }

    private static (SystemViewModel System, ChannelViewModel Channel) CreateSystem(
        string systemName,
        string channelName)
    {
        var channel = new ChannelViewModel(Channel(systemName, channelName, "100"));
        return (CreateSystem(null, [channel], systemName), channel);
    }

    private static SystemViewModel CreateSystem(
        SystemViewModel? _,
        IReadOnlyList<ChannelViewModel> channels,
        string systemName = "System")
        => new(
            new DvmConsole.FneClient.FneConnectionOptions(
                systemName, "Console", "127.0.0.1", 62031, 1, null, false, null),
            systemName,
            "127.0.0.1:62031",
            channels,
            [],
            0);

    private static ChannelConfiguration Channel(string system, string name, string tgid)
        => new() { System = system, Name = name, Tgid = tgid, Mode = "p25" };

}
