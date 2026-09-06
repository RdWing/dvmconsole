// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

[Collection(AvaloniaControlTestCollection.Name)]
public sealed class ChannelRendererControllerTests
{
    [Fact]
    public void NarrowWidthUsesListWithoutChangingSavedDesktopPreference()
    {
        var settings = new OperatorViewSettings
        {
            ChannelRenderer = ConsoleRendererPreference.Cards
        };
        var host = new ContentControl();
        var cards = new Border();
        var list = new Border();
        var cardsMenu = new MenuItem();
        var listMenu = new MenuItem();
        var controller = new ChannelRendererController(
            settings,
            host,
            cards,
            list,
            cardsMenu,
            listMenu);

        Assert.True(controller.RequiresSwitch(599));
        controller.Apply(599);

        Assert.Same(list, host.Content);
        Assert.Equal(ConsoleRendererPreference.Cards, controller.Preference);
        Assert.False(cardsMenu.IsEnabled);
        Assert.True(listMenu.IsChecked);

        Assert.True(controller.RequiresSwitch(1_180));
        controller.Apply(1_180);
        Assert.Same(cards, host.Content);
        Assert.True(cardsMenu.IsChecked);
    }
}
