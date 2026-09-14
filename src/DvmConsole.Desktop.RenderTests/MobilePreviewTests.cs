// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using DvmConsole.Mobile;
using DvmConsole.Application;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobilePreviewTests
{
    [Theory]
    [InlineData(390, ConsoleHostFormFactor.Phone, ConsoleRendererPreference.List)]
    [InlineData(852, ConsoleHostFormFactor.Phone, ConsoleRendererPreference.List)]
    [InlineData(599, ConsoleHostFormFactor.Tablet, ConsoleRendererPreference.List)]
    [InlineData(600, ConsoleHostFormFactor.Tablet, ConsoleRendererPreference.Cards)]
    [InlineData(1024, ConsoleHostFormFactor.Tablet, ConsoleRendererPreference.Cards)]
    public void DeviceFormFactorDistinguishesLandscapePhoneFromTablet(
        double width, ConsoleHostFormFactor formFactor, ConsoleRendererPreference expected)
    {
        Assert.Equal(expected, ResponsivePresentationPolicy.Resolve(
            width, ConsoleRendererPreference.Cards, formFactor).EffectiveRenderer);
    }

    [AvaloniaFact]
    public async Task TabletCardsKeepSizePreferencesAboveTheReadableTouchMinimum()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        string[] sizes = ["small", "normal", "large"];
        double[] widths = [180, 235, 330];
        var descriptors = preview.ApplicationSession.Topology.Channels.Take(3)
            .Select((channel, index) => channel with { CardSize = sizes[index] }).ToArray();
        var zone = preview.ApplicationSession.Topology.Zones[0];
        var topology = new ConsoleTopologySnapshot(null, preview.ApplicationSession.Topology.Systems,
            [new ZoneDescriptor(zone.Id, zone.Name, descriptors.Select(channel => channel.Id).ToArray())], descriptors);
        await using var session = new ConsoleApplicationSession(topology, ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Tablet, applicationSession: session, ownsSession: false);
        var window = new Window { Width = 1024, Height = 768, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var cards = view.GetVisualDescendants().OfType<ChannelCardContent>().ToArray();
            Assert.Equal(3, cards.Length);
            for (int index = 0; index < cards.Length; index++)
            {
                var model = Assert.IsType<ChannelSnapshotCardViewModel>(cards[index].DataContext);
                Assert.Equal(widths[index], model.CardWidth);
                Assert.Equal(ChannelViewModel.ResolveCardWidth(sizes[index]), model.CardWidth);
                var border = cards[index].GetVisualAncestors().OfType<Border>().First();
                Assert.Equal(Math.Max(300, widths[index]), border.Bounds.Width);
                Assert.Equal(44, cards[index].GetVisualDescendants().OfType<NeutralSnapSlider>().Single().Bounds.Height);
                Assert.InRange(cards[index].Bounds.Height, 140, 220);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TabletRestoresCardsAfterNarrowSplitViewAndHonorsExplicitListChoice()
    {
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var window = new Window { Width = 1024, Height = 768, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.Cards, view.EffectiveRenderer);
            Assert.Equal(12, view.GetVisualDescendants().OfType<ChannelCardContent>().Count());
            foreach (var card in view.GetVisualDescendants().OfType<ChannelCardContent>())
            {
                var slider = card.GetVisualDescendants().OfType<NeutralSnapSlider>().Single();
                Assert.Equal(44, slider.Bounds.Height);
                Assert.InRange(card.Bounds.Height, 140, 220);
                Assert.Equal(44, ((Grid)slider.Parent!).Bounds.Height);
            }
            window.Width = 390;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.List, view.EffectiveRenderer);
            Assert.Equal(ConsoleRendererPreference.Cards, view.SavedPreference);
            window.Width = 1024;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.Cards, view.EffectiveRenderer);
            view.SelectRenderer(ConsoleRendererPreference.List);
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.List, view.EffectiveRenderer);
            Assert.Single(view.GetVisualDescendants().OfType<ChannelListView>());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(852)]
    public async Task PhoneAlwaysUsesSharedListAndPreviewCannotTransmit(double width)
    {
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var window = new Window { Width = width, Height = 600, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            view.SelectRenderer(ConsoleRendererPreference.Cards);
            Assert.Equal(ConsoleRendererPreference.List, view.EffectiveRenderer);
            ChannelListView list = Assert.Single(view.GetVisualDescendants().OfType<ChannelListView>());
            Assert.All(list.GetVisualDescendants().OfType<Button>().Where(button => button.GetType() == typeof(Button)), button => Assert.False(button.IsEffectivelyEnabled));
            ScrollViewer scroller = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SavedLayoutSurvivesNewViewsAndNarrowFallbackDoesNotWrite()
    {
        var preferences = new LayoutPreferences();
        await using (var first = new MobileConsoleView(ConsoleHostFormFactor.Tablet, preferences))
            first.SelectRenderer(ConsoleRendererPreference.List);
        Assert.Equal(1, preferences.Writes);
        await using var reopened = new MobileConsoleView(ConsoleHostFormFactor.Tablet, preferences);
        Assert.Equal(ConsoleRendererPreference.List, reopened.SavedPreference);
        reopened.SelectRenderer(ConsoleRendererPreference.Cards);
        var window = new Window { Width = 390, Height = 768, Content = reopened };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.List, reopened.EffectiveRenderer);
            Assert.Equal(ConsoleRendererPreference.Cards, preferences.Value);
            Assert.Equal(2, preferences.Writes);
        }
        finally { window.Close(); }
        await using var phone = new MobileConsoleView(ConsoleHostFormFactor.Phone, preferences);
        phone.SelectRenderer(ConsoleRendererPreference.List);
        Assert.Equal(ConsoleRendererPreference.List, phone.SavedPreference);
        Assert.Equal(ConsoleRendererPreference.Cards, preferences.Value);
        Assert.Equal(2, preferences.Writes);
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    public async Task CompactRowsKeepMeterAndPttInsideTheirBorders(double width)
    {
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var window = new Window { Width = width, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            var row = view.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("channel-list-row"));
            foreach (var control in row.GetVisualDescendants().OfType<Control>().Where(control =>
                         control.Classes.Contains("list-meter") || control.Classes.Contains("ptt")))
            {
                var origin = control.TranslatePoint(default, row)!.Value;
                Assert.True(origin.X >= 0);
                Assert.True(origin.X + control.Bounds.Width <= row.Bounds.Width + 1);
            }
            Assert.True(row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("compact-caller")).IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsLibraryHasReturnPathAndRetainsItsPage()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var configuration = new TextBox { Text = "Draft remains available" };
        var routePicker = new Button { Content = "Host audio route picker", Width = 48, Height = 48 };
        var settings = new MobileSettingsView(configuration, ConsoleHostFormFactor.Phone, () => console,
            audioRoutePicker: routePicker);
        var window = new Window { Width = 390, Height = 700, Content = settings };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.DoesNotContain(routePicker, settings.GetVisualDescendants());
            void Click(string label) => settings.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, label)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Click("Configuration Library  ›");
            window.UpdateLayout();
            Assert.Contains(configuration, settings.GetVisualDescendants());
            Assert.DoesNotContain(routePicker, settings.GetVisualDescendants());
            Click("‹ Settings");
            window.UpdateLayout();
            Assert.DoesNotContain(routePicker, settings.GetVisualDescendants());
            Click("Configuration Library  ›");
            window.UpdateLayout();
            Assert.Contains(configuration, settings.GetVisualDescendants());
            Assert.Equal("Draft remains available", configuration.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390, 700)]
    [InlineData(852, 320)]
    [InlineData(1024, 768)]
    public async Task HistoryNavigationRetainsSessionAndBoundsExpandedFilters(double width, double height)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var session = new MobileSession(console.ApplicationSession);
        var settings = new MobileSettingsView(new TextBox(), ConsoleHostFormFactor.Phone, () => console, () => session);
        var window = new Window { Width = width, Height = height, Content = settings };
        try
        {
            window.Show();
            window.UpdateLayout();
            void Click(string label) => settings.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, label)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Click("History  ›");
            window.UpdateLayout();
            var history = Assert.Single(settings.GetVisualDescendants().OfType<CallHistoryView>());
            Assert.False(history.ExportButton.IsEnabled);
            Assert.False(history.ClearButton.IsEnabled);
            history.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            window.UpdateLayout();
            foreach (var button in new[] { history.ExportButton, history.ClearButton })
            {
                var position = button.TranslatePoint(default, history)!.Value;
                Assert.InRange(position.X, 0, history.Bounds.Width);
                Assert.True(position.X + button.Bounds.Width <= history.Bounds.Width + 1);
            }
            Assert.All(history.GetVisualDescendants().OfType<ComboBox>(), control => Assert.True(control.Bounds.Height >= 44));
            Assert.True(history.HistoryItems.Bounds.Height > 0,
                string.Join("; ", history.GetVisualDescendants().OfType<Control>()
                    .Where(control => control is Grid or ScrollViewer or Expander or ListBox)
                    .Select(control => $"{control.GetType().Name} {control.Name}: {control.Bounds}, desired {control.DesiredSize}")));
            Click("‹ Settings");
            window.UpdateLayout();
            Click("History  ›");
            window.UpdateLayout();
            Assert.Same(history, Assert.Single(settings.GetVisualDescendants().OfType<CallHistoryView>()));
            Assert.Same(session.Application, console.ApplicationSession);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LiveCardsUseInjectedSessionAndViewDoesNotRetireHostOwnedSession()
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        int disposals = 0;
        await using var session = new ConsoleApplicationSession(preview.ApplicationSession.Topology,
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands(),
            dispose: () => { disposals++; return ValueTask.CompletedTask; });
        var view = new MobileConsoleView(ConsoleHostFormFactor.Tablet, applicationSession: session, ownsSession: false);
        var window = new Window { Width = 1024, Height = 768, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Same(session, view.ApplicationSession);
            var targets = view.GetVisualDescendants().OfType<Button>().Where(button =>
                Avalonia.Automation.AutomationProperties.GetName(button)?.EndsWith("toggle listening") == true).ToArray();
            Assert.Equal(12, targets.Length);
            Assert.All(targets, target => Assert.True(target.IsEffectivelyEnabled));
            Assert.All(view.GetVisualDescendants().OfType<ChannelCardContent>(), content =>
                Assert.False(((ChannelSnapshotCardViewModel)content.DataContext!).IsPttControlEnabled));
        }
        finally { window.Close(); await view.DisposeAsync(); }
        Assert.Equal(0, disposals);
        await session.DisposeAsync();
        Assert.Equal(1, disposals);
    }

    [AvaloniaFact]
    public async Task MobileReplacementRetiresOnlyOutgoingSessionAndPublishesIncomingView()
    {
        await using var fixture = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        int oldDisposed = 0, newDisposed = 0;
        var oldExecution = new ConsoleExecutionPolicy();
        var nextExecution = new ConsoleExecutionPolicy();
        var oldSession = new ConsoleApplicationSession(fixture.ApplicationSession.Topology,
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands(),
            dispose: () => { oldDisposed++; oldExecution.Stop(); return ValueTask.CompletedTask; });
        var nextSession = new ConsoleApplicationSession(fixture.ApplicationSession.Topology,
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands(),
            dispose: () => { newDisposed++; return ValueTask.CompletedTask; });
        var oldView = new MobileConsoleView(ConsoleHostFormFactor.Phone, execution: oldExecution, applicationSession: oldSession, ownsSession: false);
        var nextView = new MobileConsoleView(ConsoleHostFormFactor.Phone, execution: nextExecution, applicationSession: nextSession, ownsSession: false);
        IConsoleSessionAttachment? published = null;
        int playbackActivations = 0;
        var next = new MobileSessionAttachment(nextView, new(nextSession, ActivateListening: _ =>
        {
            Assert.Equal(1, oldDisposed);
            Assert.Same(nextView, ((MobileSessionAttachment)published!).View);
            playbackActivations++;
            return Task.CompletedTask;
        })
        { Execution = nextExecution });
        await using var host = new ConsoleSessionHost(new MobileSessionAttachment(oldView, new(oldSession)),
            attachment => published = attachment, () => { }, () => { });
        await host.ReplaceAsync(next);
        Assert.Same(next, published);
        Assert.Equal(1, playbackActivations);
        Assert.Equal(1, oldDisposed);
        Assert.Equal(0, newDisposed);
        Assert.Same(nextExecution, next.Session.Execution);
        Assert.Equal(ConsoleExecutionState.Stopping, oldExecution.Snapshot.State);
        Assert.NotNull(next.Session.Execution!.TryAcquire(ConsoleTransmitIntent.Manual));
        await host.DisposeAsync();
        Assert.Equal(1, oldDisposed);
        Assert.Equal(1, newDisposed);
    }

    [AvaloniaFact]
    public async Task SessionAttachmentRejectsASeparatePresentationExecutionPolicy()
    {
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        Assert.Throws<ArgumentException>(() => new MobileSessionAttachment(view,
            new(view.ApplicationSession) { Execution = new ConsoleExecutionPolicy() }));
    }

    [AvaloniaFact]
    public async Task LiveLayoutChoiceIsScopedToConfigurationAndNarrowFallbackDoesNotOverwriteIt()
    {
        await using var fixture = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var preferences = new ConfigurationLayouts();
        var firstReference = new ConfigurationReference(new(Guid.NewGuid()), new(Guid.NewGuid()));
        var secondReference = new ConfigurationReference(new(Guid.NewGuid()), new(Guid.NewGuid()));
        await using var firstSession = new ConsoleApplicationSession(fixture.ApplicationSession.Topology with { Configuration = firstReference },
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var secondSession = new ConsoleApplicationSession(fixture.ApplicationSession.Topology with { Configuration = secondReference },
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var first = new MobileConsoleView(ConsoleHostFormFactor.Tablet, preferences,
            applicationSession: firstSession, ownsSession: false);
        first.SelectRenderer(ConsoleRendererPreference.List);
        await using var second = new MobileConsoleView(ConsoleHostFormFactor.Tablet, preferences,
            applicationSession: secondSession, ownsSession: false);
        Assert.Equal(ConsoleRendererPreference.Cards, second.SavedPreference);
        var window = new Window { Width = 390, Height = 700, Content = second };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.List, second.EffectiveRenderer);
            Assert.Equal(ConsoleRendererPreference.Cards, second.SavedPreference);
            Assert.Single(preferences.Values);
            window.Width = 1024;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(ConsoleRendererPreference.Cards, second.EffectiveRenderer);
        }
        finally { window.Close(); }
        await using var reopened = new MobileConsoleView(ConsoleHostFormFactor.Tablet, preferences,
            applicationSession: firstSession, ownsSession: false);
        Assert.Equal(ConsoleRendererPreference.List, reopened.SavedPreference);
    }

    private sealed class ConfigurationLayouts : IMobileLayoutPreferences
    {
        public Dictionary<string, ConsoleRendererPreference> Values { get; } = [];
        public ConsoleRendererPreference? Read(string configurationId) => Values.TryGetValue(configurationId, out var value) ? value : null;
        public void Write(string configurationId, ConsoleRendererPreference preference) => Values[configurationId] = preference;
    }

    private sealed class LayoutPreferences : IMobileLayoutPreferences
    {
        public ConsoleRendererPreference? Value { get; private set; }
        public int Writes { get; private set; }
        public ConsoleRendererPreference? Read(string configurationId) => Value;
        public void Write(string configurationId, ConsoleRendererPreference preference)
        {
            Assert.Equal("preview", configurationId);
            Value = preference;
            Writes++;
        }
    }

}
