// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileConsoleFidelityTests
{
    [Theory]
    [InlineData("Engine 42", 1007u, "Engine 42 · RID 1007")]
    [InlineData("1007", 1007u, "RID 1007")]
    [InlineData("", 1007u, "RID 1007")]
    public void CallerRetainsAliasAndUnambiguousRid(string alias, uint id, string expected)
        => Assert.Equal(expected, ChannelListItemViewModel.FormatTouchCaller(alias, id));

    [AvaloniaTheory]
    [InlineData(390, 1)]
    [InlineData(390, 1.5)]
    [InlineData(768, 1)]
    public async Task ActiveNamesRemainInTopSummaryAndFitGroupHeaders(double width, double scale)
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var original = preview.ApplicationSession.Topology;
        var descriptors = original.Channels.Take(3).Select((channel, index) => channel with
        {
            Name = index == 0 ? "Dispatch Operations North" : channel.Name,
            SystemId = index == 2 ? new SystemId("second-fne") : channel.SystemId
        }).ToArray();
        var topology = original with { Channels = descriptors };
        await using var previewModel = new ConsoleListViewModel(preview.ApplicationSession,
            new ChannelPttController(preview.ApplicationSession.Commands.BeginPttAsync, preview.ApplicationSession.Commands.EndPttAsync));
        var channels = previewModel.Items.ToDictionary(item => item.Id,
            item => item.Snapshot with { ReceiveEnabled = false, ReceiveActive = false });
        foreach (var channel in descriptors.Take(2))
            channels[channel.Id] = channels[channel.Id] with { ReceiveEnabled = true, ReceiveActive = true };
        await using var session = new ConsoleApplicationSession(topology,
            preview.ApplicationSession.Snapshot with { Channels = channels }, preview.ApplicationSession.Commands);
        var connections = new Connections(topology.Systems[0].Id);
        connections.Connect();
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            applicationSession: session, ownsSession: false, connectionStates: connections);
        var window = new Window { Width = width, Height = 844, Content = view };
        MobileTypography.Apply(window.Resources, scale);
        try
        {
            window.Show();
            window.UpdateLayout();
            string names = string.Join(", ", descriptors.Take(2).Select(channel => channel.Name));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == $"2 RX enabled · RX {names}");
            var activity = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("group-rx-label") && text.IsVisible);
            Assert.Equal(names, activity.Text);
            var position = activity.TranslatePoint(new Avalonia.Point(), window)!.Value;
            Assert.InRange(position.X + activity.Bounds.Width, 1, width);
            Assert.True(activity.Bounds.Height > 0);
            if (Environment.GetEnvironmentVariable("DVM_RX_SUMMARY_SCREENSHOT") is { } directory)
            {
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize((int)width, 844));
                bitmap.Render(window);
                bitmap.Save(System.IO.Path.Combine(directory, $"rx-summary-{width}-{scale}.png"));
            }
            channels = channels.ToDictionary(pair => pair.Key, pair => pair.Value);
            channels[descriptors[0].Id] = channels[descriptors[0].Id] with { ReceiveEnabled = false };
            session.PublishSnapshot(session.Snapshot with { Channels = channels });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == $"1 RX enabled · RX {descriptors[1].Name}");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GroupActivityAdaptsToTopologyAndCollapse(bool multipleSystems, bool multipleZones)
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var original = preview.ApplicationSession.Topology;
        var first = original.Channels[0];
        var second = original.Channels[1] with
        {
            SystemId = first.SystemId,
            ZoneId = multipleZones ? new ZoneId("second-zone") : first.ZoneId
        };
        var channels = new List<ChannelDescriptor> { first, second };
        if (multipleSystems)
            channels.Add(original.Channels[2] with { SystemId = new SystemId("second-fne") });
        var topology = original with { Channels = channels };
        await using var session = new ConsoleApplicationSession(topology, preview.ApplicationSession.Snapshot,
            preview.ApplicationSession.Commands);
        await using var model = new ConsoleListViewModel(session,
            new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync));
        var group = model.VisibleRows.OfType<ConsoleListGroupViewModel>().First();
        var zones = model.VisibleRows.OfType<ConsoleListGroupViewModel>().Where(row => !row.IsSystem).ToArray();
        Assert.Equal(multipleZones ? 2 : 0, zones.Length);
        var states = model.Items.ToDictionary(item => item.Id,
            item => item.Snapshot with { ReceiveActive = false });
        states[second.Id] = states[second.Id] with { ReceiveEnabled = true, ReceiveActive = true };
        session.PublishSnapshot(session.Snapshot with { Channels = states });
        Assert.Equal(multipleSystems || multipleZones, group.ShowReceiveDot);
        Assert.Equal(multipleSystems && !multipleZones, group.ShowReceiveNames);
        Assert.Equal(second.Name, group.ReceiveActivityText);
        if (multipleZones)
        {
            Assert.False(zones[0].ShowReceiveDot);
            Assert.True(zones[1].ShowReceiveNames);
        }
        model.ToggleGroup(group);
        Assert.Equal(multipleSystems || multipleZones, group.ShowReceiveNames);
        states = states.ToDictionary(pair => pair.Key, pair => pair.Value);
        states[first.Id] = states[first.Id] with { ReceiveEnabled = true, ReceiveActive = true };
        session.PublishSnapshot(session.Snapshot with { Channels = states });
        Assert.Equal($"{first.Name}, {second.Name}", group.ReceiveActivityText);
        states = states.ToDictionary(pair => pair.Key, pair => pair.Value);
        states[second.Id] = states[second.Id] with { ReceiveEnabled = false };
        session.PublishSnapshot(session.Snapshot with { Channels = states });
        Assert.Equal(first.Name, group.ReceiveActivityText);
        model.ToggleGroup(group);
        Assert.Equal(multipleSystems && !multipleZones, group.ShowReceiveNames);
    }

    [AvaloniaFact]
    public async Task ReceiveActivitySurvivesCollapsedGroupsAndRevealDoesNotChangeCommands()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var topology = preview.ApplicationSession.Topology;
        await using var session = new ConsoleApplicationSession(topology, preview.ApplicationSession.Snapshot,
            preview.ApplicationSession.Commands);
        await using var model = new ConsoleListViewModel(session,
            new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync));
        var item = model.Items[0];
        var group = model.VisibleRows.OfType<ConsoleListGroupViewModel>().First();
        model.ToggleGroup(group);
        Assert.DoesNotContain(item, model.VisibleRows);
        var snapshot = session.Snapshot;
        var channels = snapshot.Channels.ToDictionary(pair => pair.Key, pair => pair.Value);
        channels[item.Id] = item.Snapshot with { ReceiveEnabled = true, ReceiveActive = true };
        session.PublishSnapshot(snapshot with { Channels = channels });
        Assert.True(group.IsReceiving);
        Assert.Contains("receiving", group.AccessibilityText);
        Assert.Equal(item.Name, group.ReceiveActivityText);
        Assert.Same(item, model.RevealChannel(item.Id));
        Assert.Contains(item, model.VisibleRows);
        Assert.False(item.IsExpanded);
        channels = channels.ToDictionary(pair => pair.Key, pair => pair.Value);
        channels[item.Id] = channels[item.Id] with { ReceiveActive = false };
        session.PublishSnapshot(session.Snapshot with { Channels = channels });
        Assert.False(group.IsReceiving);
        Assert.Empty(group.ReceiveActivityText);
    }

    [AvaloniaFact]
    public async Task ConnectionNotificationsUpdateSummaryAndFneWithoutRebuildingRows()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var states = new Connections(preview.ApplicationSession.Topology.Systems[0].Id);
        var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            applicationSession: preview.ApplicationSession, ownsSession: false, connectionStates: states);
        var window = new Window { Width = 390, Height = 844, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        MobileTypography.Apply(window.Resources, 1);
        try
        {
            window.Show();
            window.UpdateLayout();
            var model = (ConsoleListViewModel)view.GetVisualDescendants().OfType<ChannelListView>().Single().DataContext!;
            var group = model.VisibleRows.OfType<ConsoleListGroupViewModel>().First();
            var item = model.Items[0];
            Assert.Equal("Disconnected", group.ConnectionText);
            var settings = view.GetVisualDescendants().OfType<Button>()
                .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Open Settings");
            var icon = Assert.IsType<PathIcon>(settings.Content);
            Assert.NotNull(icon.Data);
            Assert.True(icon.Bounds.Width > 0 && icon.Bounds.Height > 0);
            var metadata = view.GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("touch-metadata"));
            Assert.Equal(item.ChannelMetadataText, metadata.Text);
            Assert.Equal(13, metadata.FontSize);
            Assert.Equal(19.5, metadata.LineHeight);
            var receive = view.GetVisualDescendants().OfType<Button>()
                .First(button => button.Classes.Contains("list-rx"));
            Assert.Equal(48, receive.Bounds.Width);
            Assert.Equal(46, receive.Bounds.Height);
            var connectionButton = view.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("group-status"));
            connectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            connectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, states.ToggleCount);
            Assert.True(group.IsExpanded);
            Assert.False(connectionButton.IsEnabled);
            states.CompleteToggle();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(connectionButton.IsEnabled);
            states.Connect();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal("Connected", group.ConnectionText);
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "0 RX enabled · Standby");
            Assert.Equal(Color.Parse("#61D994"), Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
            Assert.Same(item, model.Items[0]);
            var rows = view.GetVisualDescendants().OfType<ListBox>().Single();
            rows.SelectedItem = item;
            window.UpdateLayout();
            var selectedPresenter = rows.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(container => ReferenceEquals(container.DataContext, item))
                .GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .First(presenter => presenter.Name == "PART_ContentPresenter");
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(selectedPresenter.Background).Color);
            item.ToggleExpansion();
            window.UpdateLayout();
            var tx = view.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "TX"));
            Assert.Equal(Color.Parse("#17212B"), Assert.IsAssignableFrom<ISolidColorBrush>(tx.Background).Color);
            await view.DisposeAsync();
            Assert.Equal(0, states.SubscriberCount);
        }
        finally { window.Close(); await view.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task TabletCardsExposeConnectionPillsAndTransparentTitleHitAreas()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var states = new Connections(preview.ApplicationSession.Topology.Systems[0].Id);
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Tablet,
            applicationSession: preview.ApplicationSession, ownsSession: false, connectionStates: states);
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DvmConsole.Mobile/"))
        {
            Source = new Uri("avares://DvmConsole.Mobile/MobileTypography.axaml")
        });
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.Cards, view.EffectiveRenderer);
            var pill = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Content is string content && content.StartsWith("DISCONNECTED\n"));
            Assert.Equal(Color.Parse("#8794A1"), Assert.IsAssignableFrom<ISolidColorBrush>(pill.BorderBrush).Color);
            pill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, states.ToggleCount);
            states.CompleteToggle();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var title = view.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("card-listen"));
            var background = title.GetVisualDescendants().OfType<Border>().First();
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(background.Background).Color);
        }
        finally { window.Close(); }
    }

    private sealed class Connections(SystemId id) : IConsoleConnectionStateNotifications, IConsoleConnectionCommands
    {
        private EventHandler? changed;
        private readonly TaskCompletionSource completion = new();
        public int ToggleCount { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreAsync(IEnumerable<SystemId> systems, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ToggleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(id, systemId);
            ToggleCount++;
            return completion.Task.WaitAsync(cancellationToken);
        }
        public void CompleteToggle() => completion.TrySetResult();
        public int SubscriberCount { get; private set; }
        public event EventHandler? ConnectionStatesChanged
        {
            add { changed += value; SubscriberCount++; }
            remove { changed -= value; SubscriberCount--; }
        }
        public ImmutableArray<RadioConnectionSnapshot> ConnectionStates { get; private set; } =
            [new(id, "System", RadioConnectionState.Disconnected, "Idle", DateTimeOffset.UnixEpoch)];
        public void Connect()
        {
            ConnectionStates = [ConnectionStates[0] with { State = RadioConnectionState.Connected }];
            changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
