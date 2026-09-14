// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileRowReorderingTests
{
    [AvaloniaFact]
    public async Task InsertionShiftsPeersAndSurvivesGroupCollapseAndRestore()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var original = preview.ApplicationSession.Topology;
        var channels = original.Channels.Take(4).Select(channel => channel with
        { SystemId = original.Channels[0].SystemId, ZoneId = original.Channels[0].ZoneId }).ToArray();
        await using var session = new ConsoleApplicationSession(original with { Channels = channels }, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        var ptt = new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync);
        await using var model = new ConsoleListViewModel(session, ptt);
        Assert.True(model.MoveWithinZone(channels[0].Id, channels[3].Id));
        var expected = new[] { channels[1].Id, channels[2].Id, channels[3].Id, channels[0].Id };
        Assert.Equal(expected, model.Items.Select(item => item.Id));
        var group = model.VisibleRows.OfType<ConsoleListGroupViewModel>().First();
        model.ToggleGroup(group);
        model.ToggleGroup(group);
        Assert.Equal(expected, model.VisibleRows.OfType<ChannelListItemViewModel>().Select(item => item.Id));
        await using var restored = new ConsoleListViewModel(session, ptt);
        restored.RestoreOrder(model.Items.Select(item => item.Id.ToString()).ToArray());
        Assert.Equal(expected, restored.Items.Select(item => item.Id));
        Assert.True(restored.MoveWithinZone(channels[0].Id, channels[1].Id));
        Assert.Equal(channels.Select(channel => channel.Id), restored.Items.Select(item => item.Id));
    }

    [AvaloniaFact]
    public async Task CrossZoneDropDoesNotChangeOrder()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var original = preview.ApplicationSession.Topology;
        var channels = original.Channels.Take(2).ToArray();
        channels[1] = channels[1] with { ZoneId = ZoneId.FromName("Different") };
        await using var session = new ConsoleApplicationSession(original with { Channels = channels }, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var model = new ConsoleListViewModel(session, new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync));
        var before = model.Items.ToArray();
        Assert.False(model.MoveWithinZone(channels[0].Id, channels[1].Id));
        Assert.Equal(before, model.Items);
    }

    [AvaloniaFact]
    public async Task HoldingRowInsertsWithoutExpandingOrInvokingControls()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var original = preview.ApplicationSession.Topology;
        var channels = original.Channels.Take(4).Select(channel => channel with
        { SystemId = original.Channels[0].SystemId, ZoneId = original.Channels[0].ZoneId }).ToArray();
        await using var session = new ConsoleApplicationSession(original with { Channels = channels }, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        var view = new ChannelListView { EnableRowReordering = true, AdaptToNarrowWidth = true };
        view.Classes.Add("touch");
        view.Attach(session, new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync));
        IReadOnlyList<string>? saved = null;
        view.RowOrderChanged += order => saved = order;
        var window = new Window { Width = 390, Height = 800, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            var rows = view.GetVisualDescendants().OfType<Border>().Where(row => row.Classes.Contains("channel-list-row")).ToArray();
            var source = rows[0];
            var start = source.TranslatePoint(new Point(12, 12), window)!.Value;
            var end = rows[3].TranslatePoint(new Point(12, 12), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            source.RaiseEvent(new HoldingRoutedEventArgs(HoldingState.Started, new(12, 12), PointerType.Touch));
            window.MouseMove(end);
            Assert.Equal(.75, source.Opacity);
            var container = source.GetVisualAncestors().OfType<ListBoxItem>().Single();
            Assert.True(container.ZIndex > rows[3].GetVisualAncestors().OfType<ListBoxItem>().Single().ZIndex);
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(end));
            Assert.True(ReferenceEquals(hit, source) || hit.GetVisualAncestors().Contains(source),
                $"Hit {hit}; ancestors {string.Join(", ", source.GetVisualAncestors().Select(v => $"{v.GetType().Name} clip={v.ClipToBounds} bounds={v.Bounds}"))}");
            window.MouseUp(end, MouseButton.Left);
            Assert.NotNull(saved);
            Assert.Equal(channels[0].Id.ToString(), saved[^1]);
            Assert.All(((ConsoleListViewModel)view.DataContext!).Items, item => Assert.False(item.IsExpanded));
            Assert.Null(source.RenderTransform);
            Assert.Null(container.RenderTransform);
            Assert.Equal(0, container.ZIndex);
        }
        finally { window.Close(); await view.DetachAsync(); }
    }
}
