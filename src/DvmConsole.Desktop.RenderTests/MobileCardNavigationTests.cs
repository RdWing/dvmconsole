// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Operations;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileCardNavigationTests
{
    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(1024)]
    public async Task BufferingReturnsToConnections(double width)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var session = new MobileSession(console.ApplicationSession);
        var settings = new MobileSettingsView(new TextBox(), ConsoleHostFormFactor.Tablet, () => console, () => session);
        var window = new Window { Width = width, Height = 900, Content = settings };
        try
        {
            window.Show();
            void Tap(string name)
            {
                settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
            Tap("Connections  ›");
            Tap("Receive Buffering  ›");
            Assert.Single(settings.GetVisualDescendants().OfType<MobileReceiveBufferingView>());
            Tap("‹ Connections");
            Assert.Empty(settings.GetVisualDescendants().OfType<MobileReceiveBufferingView>());
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Receive Buffering  ›"));
            Tap("‹ Settings");
            foreach (string category in new[] { "Audio  ›", "Push to Talk  ›", "Connections  ›" })
            {
                Tap(category);
                Tap("‹ Settings");
                Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Choose a Settings category" && text.IsEffectivelyVisible);
            }
            Tap("Audio  ›");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Console layout");
            Tap("Microphone Processing  ›");
            Tap("‹ Audio");
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Microphone Processing  ›"));

        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WideTabletPartitionsWholeSystemsAndRestoresSingleColumn()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var original = preview.ApplicationSession.Topology;
        var second = SystemId.FromName("Second");
        var channels = original.Channels.Select((channel, index) => index % 2 == 0
            ? channel : channel with { SystemId = second }).ToArray();
        var topology = original with
        {
            Channels = channels,
            Systems = [original.Systems[0], new(second, "Second", "P25")]
        };
        await using var session = new ConsoleApplicationSession(topology, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        var list = new DvmConsole.Presentation.ChannelListView { AllowMultipleColumns = true };
        var ptt = new DvmConsole.Presentation.ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync);
        list.Attach(session, ptt);
        var window = new Window { Width = 1200, Height = 700, Content = list };
        try
        {
            window.Show();
            window.UpdateLayout();
            var columns = list.GetVisualDescendants().OfType<ListBox>().Where(box => box.IsVisible).ToArray();
            Assert.Equal(2, columns.Length);
            Assert.All(columns, column => Assert.Single(column.Items.OfType<DvmConsole.Presentation.ConsoleListGroupViewModel>(), group => group.IsSystem));
            var model = (ConsoleListViewModel)list.DataContext!;
            var peers = model.Items.Where(item => item.Descriptor.SystemId == channels[0].SystemId).ToArray();
            Assert.True(model.MoveWithinZone(peers[0].Id, peers[^1].Id));
            Assert.All(columns, column => Assert.Equal(column.Items.Count, column.Items.Distinct().Count()));
            list.Width = 700;
            window.Width = 700;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Single(list.GetVisualDescendants().OfType<ListBox>(), box => box.IsVisible);
            Assert.Equal(2, list.GetVisualDescendants().OfType<ListBox>().First().Items.OfType<DvmConsole.Presentation.ConsoleListGroupViewModel>().Count(group => group.IsSystem));
        }
        finally { window.Close(); await list.DetachAsync(); }
    }

    [AvaloniaFact]
    public async Task HoldDragsAcrossGridWithoutOpeningMenuOrTogglingListening()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var topology = preview.ApplicationSession.Topology;
        var channels = topology.Channels.Take(4).ToArray();
        // One visible zone with enough cards to cross both a column and a row.
        topology = topology with
        {
            Channels = channels.Select(channel => channel with
            { SystemId = channels[0].SystemId, ZoneId = channels[0].ZoneId }).ToArray()
        };

        var handles = channels.ToDictionary(channel => channel.Id, _ => new Button { Content = "Listen" });
        var cards = channels.ToDictionary(channel => channel.Id,
            channel => (Control)new Border { Width = 180, Height = 100, Child = handles[channel.Id] });
        var panel = new MobileCardGrid(topology, cards);
        panel.SetViewport(new(400, 400));
        foreach (var card in cards.Values) panel.Children.Add(card);
        IReadOnlyDictionary<string, MobileCardPosition>? saved = null;
        var reorder = new MobileCardReordering(panel, cards, positions => saved = positions);
        int clicks = 0;
        foreach (var channel in channels)
        {
            reorder.Attach(handles[channel.Id], channel.Id);
            handles[channel.Id].Click += (_, _) => clicks++;
        }
        var window = new Window { Width = 400, Height = 400, Content = new ScrollViewer { Content = panel } };
        try
        {
            window.Show();
            window.UpdateLayout();
            var source = channels[0].Id;
            var target = channels[^1].Id;
            var start = handles[source].TranslatePoint(new Avalonia.Point(20, 20), window)!.Value;
            var end = start + new Avalonia.Vector(13, 263);
            var neighbor = panel.Position(target);
            window.MouseDown(start, MouseButton.Left);
            handles[source].RaiseEvent(new HoldingRoutedEventArgs(HoldingState.Started, new(20, 20), PointerType.Touch));
            window.MouseMove(end);
            Assert.Null(handles[source].ContextMenu);
            var offset = Assert.IsType<TranslateTransform>(cards[source].RenderTransform);
            Assert.Equal(end.X - start.X, offset.X);
            Assert.Equal(end.Y - start.Y, offset.Y);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(new MobileCardPosition(10, 260), panel.Position(source));
            Assert.Equal(neighbor, panel.Position(target));
            Assert.NotNull(saved);
            Assert.Equal(new MobileCardPosition(10, 260), saved[source.ToString()]);
            Assert.Null(cards[source].RenderTransform);
            Assert.Equal(1, cards[source].Opacity);
            Assert.Equal(0, clicks);
            var restored = new MobileCardGrid(topology, cards);
            restored.Restore(saved);
            Assert.Equal(panel.Position(source), restored.Position(source));
            window.UpdateLayout();
            var tap = handles[source].TranslatePoint(new Avalonia.Point(20, 20), window)!.Value;
            window.MouseDown(tap, MouseButton.Left);
            window.MouseUp(tap, MouseButton.Left);
            Assert.Equal(1, clicks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GridRestoresHolesAcrossViewportChangesAndRejectsInvalidCoordinates()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var topology = preview.ApplicationSession.Topology;
        var channels = topology.Channels.Take(2).ToArray();
        var cards = channels.ToDictionary(channel => channel.Id, _ => (Control)new Border { Width = 180, Height = 100 });
        var panel = new MobileCardGrid(topology, cards);
        foreach (var card in cards.Values) panel.Children.Add(card);
        panel.Restore(new Dictionary<string, MobileCardPosition>
        {
            [channels[0].Id.ToString()] = new(710, 530),
            [channels[1].Id.ToString()] = new(double.NaN, -10)
        });
        panel.SetViewport(new(400, 400));
        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(panel.DesiredSize));
        Assert.Equal(new MobileCardPosition(710, 530), panel.Position(channels[0].Id));
        Assert.Equal(new MobileCardPosition(0, 0), panel.Position(channels[1].Id));
        panel.SetViewport(new(900, 700));
        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(panel.DesiredSize));
        Assert.Equal(new MobileCardPosition(710, 530), panel.Position(channels[0].Id));
        Assert.Equal(new MobileCardPosition(0, 0), panel.Position(channels[1].Id));
        Assert.True(panel.DesiredSize.Width > 890);
        Assert.Equal(new MobileCardPosition(0, 20), MobileCardPosition.Snap(-17, 17));
    }

    [AvaloniaFact]
    public void TabsFilterCardsAndRememberZoneWhenSwitchingSystems()
    {
        var a = SystemId.FromName("A");
        var b = SystemId.FromName("B");
        var x = ZoneId.FromName("X");
        var y = ZoneId.FromName("Y");
        var z = ZoneId.FromName("Z");
        var channels = new[] {
            new ChannelDescriptor(new ChannelId(new ChannelSessionId("Test", ChannelProtocol.P25, 1, 0, "one")), a, x, "One", 1, "P25", 0, false),
            new ChannelDescriptor(new ChannelId(new ChannelSessionId("Test", ChannelProtocol.P25, 2, 0, "two")), a, y, "Two", 2, "P25", 0, false),
            new ChannelDescriptor(new ChannelId(new ChannelSessionId("Test", ChannelProtocol.P25, 3, 0, "three")), b, z, "Three", 3, "P25", 0, false) };
        var topology = new ConsoleTopologySnapshot(null,
            [new(a, "A", "P25"), new(b, "B", "P25")],
            [new(x, "X", [channels[0].Id]), new(y, "Y", [channels[1].Id]), new(z, "Z", [channels[2].Id])], channels);
        var cards = channels.ToDictionary(channel => channel.Id, _ => (Control)new Border());
        var navigation = new MobileCardNavigation(topology, cards);
        var window = new Window { Content = navigation };
        window.Show();
        void Tap(string name) => navigation.GetVisualDescendants().OfType<ToggleButton>()
            .Single(button => Equals(button.Content, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(cards[channels[0].Id].IsVisible);
        Assert.False(cards[channels[1].Id].IsVisible);
        Tap("Y");
        Tap("B");
        Assert.True(cards[channels[2].Id].IsVisible);
        Tap("A");
        Assert.True(cards[channels[1].Id].IsVisible);
        Assert.False(cards[channels[0].Id].IsVisible);
        navigation.Reveal(channels[2].Id);
        Assert.True(cards[channels[2].Id].IsVisible);
        window.Close();
    }
}
