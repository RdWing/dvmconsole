using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using DvmConsole.Presentation;
using System.Windows.Input;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class PresentationRenderTests
{
    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(390, true)]
    public void ConfigurationLibraryIsResponsiveAccessibleAndRestorable(double width, bool dark)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var viewModel = new ConfigurationLibraryViewModel();
        viewModel.Replace(
        [
            new ConfigurationSummary(
                ConfigurationId.New(),
                "Regional Dispatch Configuration With A Deliberately Long Name",
                ConfigurationRevision.New(),
                now,
                IsActive: true,
                PendingReload: true,
                IsReadOnly: false,
                IsLegacyCandidate: false),
            new ConfigurationSummary(
                ConfigurationId.New(),
                "Event Operations",
                ConfigurationRevision.New(),
                now.AddMinutes(-10),
                IsActive: false,
                PendingReload: false,
                IsReadOnly: false,
                IsLegacyCandidate: true)
        ],
        [
            new ConfigurationSummary(
                ConfigurationId.New(),
                "Recoverable Training Configuration",
                ConfigurationRevision.New(),
                now.AddDays(-1),
                IsActive: false,
                PendingReload: false,
                IsReadOnly: false,
                IsLegacyCandidate: false)
        ]);
        var view = new ConfigurationLibraryView { DataContext = viewModel };
        var host = new Window
        {
            Width = width,
            Height = 700,
            Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            Expander trash = Assert.Single(view.GetVisualDescendants().OfType<Expander>());
            trash.IsExpanded = true;
            host.UpdateLayout();

            ScrollViewer outer = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(
                outer.Extent.Width <= outer.Viewport.Width + 1,
                $"Configuration Library extent {outer.Extent.Width} exceeded viewport {outer.Viewport.Width} at {width} logical pixels.");
            Assert.Equal(2, viewModel.Configurations.Count);
            Assert.Single(viewModel.Trash);
            Button[] buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.GetType() == typeof(Button))
                .ToArray();
            Assert.NotEmpty(buttons);
            Assert.All(buttons, button =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
            if (width < 400)
            {
                Assert.All(buttons, button => Assert.True(
                    button.Bounds.Height >= 44,
                    $"Configuration Library button was {button.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [Theory]
    [InlineData(1440, ConsoleRendererPreference.Cards, ConsoleRendererPreference.Cards, ResponsivePresentationState.Wide, false)]
    [InlineData(1120, ConsoleRendererPreference.Cards, ConsoleRendererPreference.Cards, ResponsivePresentationState.Wide, false)]
    [InlineData(880, ConsoleRendererPreference.Cards, ConsoleRendererPreference.Cards, ResponsivePresentationState.DesktopCompact, false)]
    [InlineData(600, ConsoleRendererPreference.Cards, ConsoleRendererPreference.Cards, ResponsivePresentationState.Narrow, false)]
    [InlineData(599, ConsoleRendererPreference.Cards, ConsoleRendererPreference.List, ResponsivePresentationState.Phone, false)]
    [InlineData(430, ConsoleRendererPreference.Cards, ConsoleRendererPreference.List, ResponsivePresentationState.Phone, false)]
    [InlineData(390, ConsoleRendererPreference.Cards, ConsoleRendererPreference.List, ResponsivePresentationState.Phone, true)]
    [InlineData(360, ConsoleRendererPreference.Cards, ConsoleRendererPreference.List, ResponsivePresentationState.Phone, true)]
    public void ResponsivePolicyUsesExactLogicalBreakpointsWithoutOverwritingPreference(
        double width,
        ConsoleRendererPreference saved,
        ConsoleRendererPreference expectedRenderer,
        ResponsivePresentationState expectedState,
        bool tightPhone)
    {
        ResponsivePresentation result = ResponsivePresentationPolicy.Resolve(width, saved);

        Assert.Equal(expectedRenderer, result.EffectiveRenderer);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(tightPhone, result.TightPhone);
        Assert.Equal(ConsoleRendererPreference.Cards, saved);
    }

    [AvaloniaTheory]
    [InlineData(1440, false)]
    [InlineData(1120, true)]
    [InlineData(880, false)]
    [InlineData(600, true)]
    [InlineData(599, false)]
    [InlineData(430, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public async Task VirtualizedListHasNoHorizontalOverflowAtAcceptanceWidths(double width, bool dark)
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 34);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var list = new ChannelListView();
        list.Attach(session, ptt);
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = list,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = Assert.Single(
                list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Horizontal extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Assert.NotEmpty(list.GetVisualDescendants().OfType<ListBoxItem>());
            Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 34);
        }
        finally
        {
            await list.DetachAsync();
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880)]
    [InlineData(600)]
    [InlineData(599)]
    [InlineData(390)]
    public async Task VirtualizedListHasNoHorizontalOverflowAtOnePointFiveUiScale(double width)
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 34);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var list = new ChannelListView();
        list.Attach(session, ptt);
        var scaled = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(1.5, 1.5),
            Child = list
        };
        var host = new Window { Width = width, Height = 760, Content = scaled };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Scaled horizontal extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
        }
        finally
        {
            await list.DetachAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task PhoneListKeepsPttVisibleAndNamesEveryInteractiveChannelControl()
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 4);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var list = new ChannelListView();
        list.Attach(session, ptt);
        var host = new Window { Width = 390, Height = 760, Content = list };

        try
        {
            host.Show();
            host.UpdateLayout();
            var model = Assert.IsType<ConsoleListViewModel>(list.DataContext);
            model.Items[0].ToggleExpansion();
            host.UpdateLayout();

            ListBoxItem row = Assert.IsType<ListBoxItem>(
                list.GetVisualDescendants().OfType<ListBoxItem>().First());
            Button pttButton = Assert.Single(
                row.GetVisualDescendants().OfType<Button>(),
                button => string.Equals(button.Content as string, "PTT", StringComparison.Ordinal));
            Assert.True(pttButton.Bounds.Width > 0);
            Assert.True(pttButton.Bounds.Height >= 44);

            Control[] interactive = row.GetVisualDescendants().OfType<Control>()
                .Where(control => control.GetType() == typeof(Button) || control is Slider)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
        }
        finally
        {
            await list.DetachAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task ListPttPointerInputSupportsHoldAndToggleModes()
    {
        var holdCommands = new RecordingConsoleCommands();
        await using ConsoleApplicationSession holdSession = CreateSession(1, holdCommands);
        await using var holdPtt = new ChannelPttController(
            holdCommands.BeginPttAsync,
            holdCommands.EndPttAsync);
        var holdList = new ChannelListView();
        holdList.Attach(holdSession, holdPtt, static () => false);
        var holdHost = new Window { Width = 880, Height = 300, Content = holdList };

        try
        {
            holdHost.Show();
            holdHost.UpdateLayout();
            Button holdButton = FindListPttButton(holdList);
            Point holdPoint = holdButton.TranslatePoint(
                new Point(holdButton.Bounds.Width / 2, holdButton.Bounds.Height / 2),
                holdHost)!.Value;

            holdHost.MouseDown(holdPoint, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, holdCommands.PttStarts);
            Assert.Equal(0, holdCommands.PttStops);

            holdHost.MouseUp(holdPoint, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, holdCommands.PttStops);
        }
        finally
        {
            await holdList.DetachAsync();
            holdHost.Close();
        }

        var toggleCommands = new RecordingConsoleCommands();
        await using ConsoleApplicationSession toggleSession = CreateSession(1, toggleCommands);
        await using var togglePtt = new ChannelPttController(
            toggleCommands.BeginPttAsync,
            toggleCommands.EndPttAsync);
        var toggleList = new ChannelListView();
        toggleList.Attach(toggleSession, togglePtt, static () => true);
        var toggleHost = new Window { Width = 880, Height = 300, Content = toggleList };

        try
        {
            toggleHost.Show();
            toggleHost.UpdateLayout();
            Button toggleButton = FindListPttButton(toggleList);
            Point togglePoint = toggleButton.TranslatePoint(
                new Point(toggleButton.Bounds.Width / 2, toggleButton.Bounds.Height / 2),
                toggleHost)!.Value;

            toggleHost.MouseDown(togglePoint, MouseButton.Left, RawInputModifiers.None);
            toggleHost.MouseUp(togglePoint, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, toggleCommands.PttStarts);
            Assert.Equal(0, toggleCommands.PttStops);

            toggleHost.MouseDown(togglePoint, MouseButton.Left, RawInputModifiers.None);
            toggleHost.MouseUp(togglePoint, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(1, toggleCommands.PttStarts);
            Assert.Equal(1, toggleCommands.PttStops);
        }
        finally
        {
            await toggleList.DetachAsync();
            toggleHost.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExpandingAListRowPreservesItsViewportPosition()
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 34);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var list = new ChannelListView();
        list.Attach(session, ptt);
        var host = new Window { Width = 880, Height = 320, Content = list };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
            scroller.Offset = new Vector(0, 700);
            host.UpdateLayout();
            Border row = list.GetVisualDescendants()
                .OfType<Border>()
                .Where(candidate => candidate.Classes.Contains("channel-list-row"))
                .First(candidate => candidate.TranslatePoint(default, scroller) is Point position &&
                    position.Y >= 0 && position.Y + candidate.Bounds.Height <= scroller.Viewport.Height);
            object? rowItem = row.DataContext;
            double initialY = row.TranslatePoint(default, scroller)!.Value.Y;
            Point clickPoint = row.TranslatePoint(new Point(150, 24), host)!.Value;

            host.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.None);
            host.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            host.UpdateLayout();

            Border restoredRow = Assert.Single(
                list.GetVisualDescendants().OfType<Border>(),
                candidate => candidate.Classes.Contains("channel-list-row") &&
                    ReferenceEquals(candidate.DataContext, rowItem));
            double restoredY = restoredRow.TranslatePoint(default, scroller)!.Value.Y;
            Assert.Equal(initialY, restoredY, precision: 1);
            Assert.True(Assert.IsType<ChannelListItemViewModel>(rowItem).IsExpanded);
        }
        finally
        {
            await list.DetachAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task ListGroupsChannelsAndClipsTheMeterAtFixedColorThresholds()
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 4);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var list = new ChannelListView();
        list.Attach(session, ptt);
        var host = new Window { Width = 880, Height = 600, Content = list };

        try
        {
            host.Show();
            session.PublishMeterSample(new ChannelMeterSample(
                session.Topology.Channels[0].Id,
                Rms: 50,
                Peak: 65,
                DateTimeOffset.UtcNow));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            host.UpdateLayout();

            var model = Assert.IsType<ConsoleListViewModel>(list.DataContext);
            Assert.Equal("Test System", model.Items[0].SystemHeading);
            Assert.Equal("Test Zone", model.Items[0].ZoneHeading);
            Assert.Null(model.Items[1].SystemHeading);
            Assert.Null(model.Items[1].ZoneHeading);
            model.Items[0].ToggleExpansion();
            host.UpdateLayout();
            Button secure = Assert.Single(
                list.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.DataContext, model.Items[0]) &&
                          string.Equals(button.Content as string, "SECURE", StringComparison.Ordinal));
            Assert.True(secure.IsEffectivelyVisible);
            NeutralSnapSlider volume = Assert.Single(
                list.GetVisualDescendants().OfType<NeutralSnapSlider>(),
                slider => ReferenceEquals(slider.DataContext, model.Items[0]));
            Assert.Equal(-1, volume.Minimum);
            Assert.Equal(1, volume.Maximum);
            Assert.Equal(0, volume.NeutralValue);
            Assert.Equal(0, volume.Value);

            string[] visibleText = list.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
            Assert.Contains("Test System", visibleText);
            Assert.Contains("Test Zone", visibleText);
            Assert.Contains("Volume 1.00×", visibleText);
            Assert.DoesNotContain("Center", visibleText);
            Assert.DoesNotContain("Authority available", visibleText);
            Assert.DoesNotContain("Authority pending", visibleText);

            Border clip = list.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("list-meter-fill-clip"));
            Border gradient = Assert.IsType<Border>(clip.Child);
            Assert.Contains("list-meter-gradient", gradient.Classes);
            Assert.Equal(50, clip.Bounds.Width, precision: 1);
            Assert.Equal(100, gradient.Bounds.Width, precision: 1);
        }
        finally
        {
            await list.DetachAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public void NeutralSliderReportsKeyboardChangesWithoutPointerHover()
    {
        var slider = new NeutralSnapSlider
        {
            Minimum = -1,
            Maximum = 1,
            Value = 0,
            SmallChange = 0.1
        };
        var host = new Window { Width = 300, Height = 100, Content = slider };
        int operatorChanges = 0;
        slider.OperatorValueChanged += (_, _) => operatorChanges++;

        try
        {
            host.Show();
            slider.Focus();
            slider.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Right
            });

            Assert.False(slider.IsPointerOver);
            Assert.True(slider.Value > 0);
            Assert.Equal(1, operatorChanges);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task ListCoalescesQueuedMeterSamplesToTheLatestValuePerChannel()
    {
        await using ConsoleApplicationSession session = CreateSession(channelCount: 1);
        await using var ptt = new ChannelPttController(
            static (_, _) => ValueTask.FromResult(true),
            static (_, _) => ValueTask.CompletedTask);
        var model = new ConsoleListViewModel(session, ptt);
        int propertyChanges = 0;
        model.Items[0].PropertyChanged += (_, _) => propertyChanges++;
        ChannelId channelId = session.Topology.Channels[0].Id;

        session.PublishMeterSample(new ChannelMeterSample(channelId, 10, 20, DateTimeOffset.UtcNow));
        session.PublishMeterSample(new ChannelMeterSample(channelId, 30, 40, DateTimeOffset.UtcNow));
        session.PublishMeterSample(new ChannelMeterSample(channelId, 50, 60, DateTimeOffset.UtcNow));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        Assert.Equal(3, propertyChanges);
        Assert.Equal(50, model.Items[0].MeterRmsWidth);
        await model.DisposeAsync();
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedHistoryPageWrapsWithoutHorizontalOverflow(double width, bool dark)
    {
        var history = new CallHistoryView
        {
            DataContext = new TestCallHistoryViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 700,
            Content = history,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        host.Resources["CardBackgroundBrush"] = Brushes.Transparent;
        host.Resources["ControlBorderBrush"] = Brushes.Gray;

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = history.HistoryItems
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .First();
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"History extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Assert.NotEmpty(history.HistoryItems.GetVisualDescendants().OfType<ListBoxItem>());
            string[] visibleText = history.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
            Assert.DoesNotContain(visibleText, text => text.Contains("20260830-123456", StringComparison.Ordinal));
            WrapPanel actions = Assert.Single(
                history.GetVisualDescendants().OfType<WrapPanel>(),
                panel => panel.Classes.Contains("history-actions"));
            Grid row = Assert.IsType<Grid>(actions.Parent);
            if (width >= 600)
            {
                Assert.Equal(2, Grid.GetColumn(actions));
                Assert.True(actions.Bounds.Right <= row.Bounds.Width + 1);
            }
            else
            {
                Assert.Equal(3, Grid.GetRow(actions));
                Assert.Equal(0, Grid.GetColumn(actions));
            }
            Assert.All(
                history.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.GetType() == typeof(Button)),
                button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedAudioSettingsPageWrapsWithoutOuterHorizontalOverflow(double width, bool dark)
    {
        var audio = new AudioSettingsView
        {
            DataContext = new TestAudioSettingsViewModel()
        };
        var outerScroller = new ScrollViewer
        {
            Content = audio,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = outerScroller,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.True(
                outerScroller.Extent.Width <= outerScroller.Viewport.Width + 1,
                $"Audio settings extent {outerScroller.Extent.Width} exceeded viewport {outerScroller.Viewport.Width} at {width} logical pixels.");
            Assert.NotEmpty(audio.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.Equal(1.2m, audio.FindControl<NumericUpDown>("MicrophoneGain")!.Value);
            Assert.Equal(2m, audio.FindControl<NumericUpDown>("MicrophoneLowGain")!.Value);
            Assert.Equal(0m, audio.FindControl<NumericUpDown>("MicrophoneMidGain")!.Value);
            Assert.Equal(-1m, audio.FindControl<NumericUpDown>("MicrophoneHighGain")!.Value);
            Assert.Equal(-25m, audio.FindControl<NumericUpDown>("MicrophoneAgcTarget")!.Value);
            if (width >= 880)
            {
                Border[] routeRows = audio.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("audio-route-row"))
                    .ToArray();
                Assert.NotEmpty(routeRows);
                Assert.All(routeRows, routeRow => Assert.True(routeRow.Bounds.Height <= 60,
                    $"Desktop audio route row was unexpectedly tall at {routeRow.Bounds.Height} pixels."));
            }
            Assert.All(
                audio.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.GetType() == typeof(Button)),
                button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedGeneralSettingsPageWrapsWithoutHorizontalOverflow(double width, bool dark)
    {
        var general = new GeneralSettingsView
        {
            DataContext = new TestGeneralSettingsViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = general,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = Assert.Single(
                general.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"General settings extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Assert.Equal(4, general.GetVisualDescendants().OfType<ComboBox>().Count());
            Assert.All(
                general.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.GetType() == typeof(Button)),
                button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));

            if (width < 400)
            {
                Control[] touchControls = general.GetVisualDescendants().OfType<Control>()
                    .Where(control => control.GetType() == typeof(Button)
                        || control is CheckBox
                        || control is Slider)
                    .ToArray();
                Assert.NotEmpty(touchControls);
                Assert.All(touchControls, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedWebStreamsPageWrapsWithoutHorizontalOverflow(double width, bool dark)
    {
        var streams = new WebStreamsSettingsView
        {
            DataContext = new TestWebStreamsSettingsViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = streams,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = Assert.Single(
                streams.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Web Streams extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Assert.Equal(2, streams.GetVisualDescendants().OfType<ComboBox>().Count());
            Control[] interactive = streams.GetVisualDescendants().OfType<Control>()
                .Where(control => control.GetType() == typeof(Button)
                    || control is Slider
                    || control is ComboBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));

            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedRecorderPageWrapsWithoutHorizontalOverflow(double width, bool dark)
    {
        var recorder = new RecorderSettingsView
        {
            DataContext = new TestRecorderSettingsViewModel(externalLocationAvailable: true)
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = recorder,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = recorder.FindControl<ScrollViewer>("RecorderScroller")!;
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Recorder extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Control[] interactive = recorder.GetVisualDescendants().OfType<Control>()
                .Where(control => control.GetType() == typeof(Button)
                    || control is ToggleButton
                    || control is TextBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));

            Border[] channelRows = recorder.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("recorder-channel-row"))
                .ToArray();
            Assert.NotEmpty(channelRows);
            if (width >= 880)
            {
                Assert.All(channelRows, channelRow => Assert.True(
                    channelRow.Bounds.Height <= 60,
                    $"Desktop recorder row was unexpectedly tall at {channelRow.Bounds.Height} pixels."));
            }

            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void SharedRecorderPageOmitsDesktopLocationControlsWhenCapabilityIsAbsent()
    {
        var recorder = new RecorderSettingsView
        {
            DataContext = new TestRecorderSettingsViewModel(externalLocationAvailable: false)
        };
        var host = new Window { Width = 390, Height = 760, Content = recorder };

        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.DoesNotContain(
                recorder.GetVisualDescendants().OfType<Button>(),
                button => string.Equals(
                    AutomationProperties.GetName(button),
                    "Choose recording location",
                    StringComparison.Ordinal) && button.IsEffectivelyVisible);
            Assert.Contains(
                recorder.GetVisualDescendants().OfType<Button>(),
                button => string.Equals(
                    AutomationProperties.GetName(button),
                    "Apply recording retention and prune",
                    StringComparison.Ordinal));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedPttPageWrapsDesktopCapabilitiesWithoutHorizontalOverflow(double width, bool dark)
    {
        var pttSettings = new PttSettingsView
        {
            DataContext = new TestPttSettingsViewModel(
                keyboardAvailable: true,
                serialAvailable: true)
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = pttSettings,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = pttSettings.FindControl<ScrollViewer>("PttSettingsScroller")!;
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"PTT settings extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Control[] interactive = pttSettings.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible)
                .Where(control => control.GetType() == typeof(Button)
                    || control is CheckBox
                    || control is ComboBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void SharedPttPageOmitsKeyboardAndSerialControlsWhenCapabilitiesAreAbsent()
    {
        var pttSettings = new PttSettingsView
        {
            DataContext = new TestPttSettingsViewModel(
                keyboardAvailable: false,
                serialAvailable: false)
        };
        var host = new Window { Width = 390, Height = 760, Content = pttSettings };

        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.DoesNotContain(
                pttSettings.GetVisualDescendants().OfType<ComboBox>(),
                control => control.IsEffectivelyVisible);
            Assert.DoesNotContain(
                pttSettings.GetVisualDescendants().OfType<Button>(),
                control => control.IsEffectivelyVisible);
            Assert.Contains(
                pttSettings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible &&
                    (text.Text?.Contains("On-screen channel PTT", StringComparison.Ordinal) ?? false));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedTonePageWrapsWithoutHorizontalOverflow(double width, bool dark)
    {
        var tones = new ToneSettingsView
        {
            DataContext = new TestToneSettingsViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = tones,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = tones.FindControl<ScrollViewer>("ToneSettingsScroller")!;
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Tone settings extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Control[] interactive = tones.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible)
                .Where(control => control.GetType() == typeof(Button)
                    || control is CheckBox
                    || control is TextBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));

            string[] visibleText = tones.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
            Assert.Contains("Station alert", visibleText);
            Assert.Contains("Evacuation announcement", visibleText);
            Assert.DoesNotContain(visibleText, text => text.Contains("1000Hz", StringComparison.Ordinal));
            Assert.DoesNotContain(visibleText, text => text.Contains("evacuation-message.opus", StringComparison.Ordinal));
            Button[] sendButtons = tones.GetVisualDescendants().OfType<Button>()
                .Where(button => (AutomationProperties.GetName(button) ?? string.Empty)
                    .StartsWith("Send ", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(sendButtons);
            Assert.All(sendButtons, button =>
                Assert.False(ScrollViewer.GetBringIntoViewOnFocusChange(button)));

            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedGroupPageWrapsExpandedEditorsWithoutHorizontalOverflow(double width, bool dark)
    {
        var groups = new GroupSettingsView
        {
            DataContext = new TestGroupSettingsViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = groups,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            foreach (Expander expander in groups.GetVisualDescendants().OfType<Expander>())
                expander.IsExpanded = true;
            host.UpdateLayout();

            ScrollViewer scroller = groups.FindControl<ScrollViewer>("GroupSettingsScroller")!;
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Group settings extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Control[] interactive = groups.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible)
                .Where(control => control.GetType() == typeof(Button)
                    || control is CheckBox
                    || control is ComboBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));

            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedConnectionsPageWrapsAndRevealsKeyStatusWithoutHorizontalOverflow(double width, bool dark)
    {
        var connections = new ConnectionsSettingsView
        {
            DataContext = new TestConnectionsSettingsViewModel()
        };
        var host = new Window
        {
            Width = width,
            Height = 760,
            Content = connections,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            ScrollViewer scroller = connections.FindControl<ScrollViewer>("ConnectionsScrollViewer")!;
            Assert.True(
                scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"Connections extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
            Assert.True(connections.TryBringKeyStatusIntoView());
            Assert.True(scroller.Offset.Y > 0);
            Control[] interactive = connections.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible)
                .Where(control => control.GetType() == typeof(Button) || control is ComboBox)
                .ToArray();
            Assert.NotEmpty(interactive);
            Assert.All(interactive, control =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));

            if (width < 400)
            {
                Assert.All(interactive, control => Assert.True(
                    control.Bounds.Height >= 44,
                    $"{control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task OperatorToolsResolvesNestedNavigationAndSafelyDropsDeferredWorkAfterClose()
    {
        var window = new OperatorToolsWindow();
        try
        {
            window.SelectSection(OperatorToolSection.EncryptionKeys);
            window.Show();
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();

            Assert.False(
                window.IsPendingSectionNavigation,
                window.PendingSectionNavigationDiagnostic);
            Assert.Single(window.GetVisualDescendants().OfType<ConnectionsSettingsView>());
            Assert.Empty(window.GetVisualDescendants().OfType<RecorderSettingsView>());
            Assert.Empty(window.GetVisualDescendants().OfType<CallHistoryView>());

            window.SelectSection(OperatorToolSection.Clock);
            window.UpdateLayout();
            Assert.Single(window.GetVisualDescendants().OfType<GeneralSettingsView>());
            Assert.Empty(window.GetVisualDescendants().OfType<ConnectionsSettingsView>());

            ContentControl host = window.FindControl<ContentControl>("ToolContent")!;
            (OperatorToolSection Section, Type HostType, Type PageType)[] pages =
            [
                (OperatorToolSection.General, typeof(GeneralSettingsView), typeof(GeneralSettingsView)),
                (OperatorToolSection.Audio, typeof(ScrollViewer), typeof(AudioSettingsView)),
                (OperatorToolSection.Tones, typeof(ToneSettingsView), typeof(ToneSettingsView)),
                (OperatorToolSection.Streams, typeof(WebStreamsSettingsView), typeof(WebStreamsSettingsView)),
                (OperatorToolSection.Recorder, typeof(RecorderSettingsView), typeof(RecorderSettingsView)),
                (OperatorToolSection.History, typeof(CallHistoryView), typeof(CallHistoryView)),
                (OperatorToolSection.Groups, typeof(GroupSettingsView), typeof(GroupSettingsView)),
                (OperatorToolSection.Connections, typeof(ConnectionsSettingsView), typeof(ConnectionsSettingsView)),
                (OperatorToolSection.Ptt, typeof(PttSettingsView), typeof(PttSettingsView))
            ];
            foreach ((OperatorToolSection section, Type hostType, Type pageType) in pages)
            {
                window.SelectSection(section);
                window.UpdateLayout();
                Assert.IsType(hostType, host.Content);
                Assert.Equal(1, window.GetVisualDescendants().Count(control => control.GetType() == pageType));
            }
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
        }
    }

    private static Button FindListPttButton(ChannelListView list)
        => Assert.Single(
            list.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("ptt"));

    private static ConsoleApplicationSession CreateSession(
        int channelCount,
        IConsoleCommands? commands = null)
    {
        var systemId = SystemId.FromName("Test System");
        var zoneId = ZoneId.FromName("Test Zone");
        ChannelDescriptor[] descriptors = Enumerable.Range(1, channelCount)
            .Select(index =>
            {
                ChannelProtocol protocol = index % 3 == 0 ? ChannelProtocol.Dmr : ChannelProtocol.P25;
                byte slot = protocol == ChannelProtocol.Dmr ? (byte)(index % 2) : (byte)0;
                var id = new ChannelId(new ChannelSessionId(
                    "Test System",
                    protocol,
                    (uint)(3000 + index),
                    slot,
                    $"channel-{index}"));
                return new ChannelDescriptor(
                    id,
                    systemId,
                    zoneId,
                    index == 2
                        ? "An exceptionally long dispatch channel name used to verify trimming and wrapping"
                        : $"Channel {index}",
                    (uint)(3000 + index),
                    protocol.ToString(),
                    slot,
                    ReceiveOnly: index == 7);
            })
            .ToArray();
        var topology = new ConsoleTopologySnapshot(
            null,
            [new SystemDescriptor(systemId, "Test System", "Mixed")],
            [new ZoneDescriptor(zoneId, "Test Zone", descriptors.Select(channel => channel.Id).ToArray())],
            descriptors);
        Dictionary<ChannelId, ChannelControlSnapshot> states = descriptors.ToDictionary(
            descriptor => descriptor.Id,
            descriptor => CreateState(descriptor, descriptors.ToList().IndexOf(descriptor)));
        return new ConsoleApplicationSession(
            topology,
            new ConsoleRuntimeSnapshot(1, null, states, false, "Ready"),
            commands ?? new NoOpConsoleCommands());
    }

    private static ChannelControlSnapshot CreateState(ChannelDescriptor descriptor, int index)
        => new(
            descriptor.Id,
            index == 1 ? ChannelRuntimeState.Receiving : index == 2 ? ChannelRuntimeState.Transmitting : ChannelRuntimeState.Idle,
            index == 1 ? "Receiving from 1001" : index == 2 ? "Transmitting" : "Idle",
            index == 0 ? "A very long subscriber alias that must trim safely" : "1001",
            ReceiveEnabled: index % 2 == 0,
            ReceiveActive: index == 1,
            Transmitting: index == 2,
            TransmitSelected: index % 4 == 0,
            PageSelected: index % 5 == 0,
            AlertSelected: index % 6 == 0,
            Recording: index == 3,
            RecordingFinalizing: index == 4,
            RecordingFault: index == 5 ? "storage unavailable" : null,
            TarArmed: index is 3 or 4 or 5,
            OutputRoute: index % 2 == 0 ? "default" : "Operator headset",
            Gain: 1,
            Balance: 0,
            EffectiveMuteReason: index == 8 ? "zone Test Zone output mute" : null,
            Authority: index == 6 ? TargetAuthorityState.Unavailable : TargetAuthorityState.Available,
            AuthorityReason: index == 6 ? "the FNE does not allow TG 3007 on TS2" : null,
            ObservedReceiveEncrypted: index == 9,
            SelectedTransmitEncrypted: index == 10,
            TransmitKeyAvailable: index != 11,
            Patches: [],
            PendingOperation: index == 12 ? "Applying route" : null,
            Fault: null,
            TransmitEncryptionConfigured: index == 0,
            TransmitEncryptionSelectable: index == 0);

    private sealed class RecordingConsoleCommands : IConsoleCommands
    {
        public int PttStarts { get; private set; }
        public int PttStops { get; private set; }

        public ValueTask SetReceiveEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<bool> BeginPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            PttStarts++;
            return ValueTask.FromResult(true);
        }

        public ValueTask EndPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            PttStops++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTransmitSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetPageSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetAlertSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetTransmitEncryptedAsync(ChannelId channelId, bool encrypted, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetChannelGainAsync(ChannelId channelId, double gain, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetChannelBalanceAsync(ChannelId channelId, double balance, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class TestCallHistoryViewModel : ICallHistoryViewModel
    {
        private static readonly string[] All = ["All"];
        private readonly ICallHistoryItemViewModel[] entries =
        [
            new TestCallHistoryItem(
                "12:34:56",
                "2026-08-30",
                "An exceptionally long mutual-aid dispatch channel name",
                "Regional Interoperability System",
                "RX",
                "P25",
                "1m 23s",
                "Long subscriber alias to TG 12345",
                "AES-256 key 1234",
                true,
                true,
                "20260830-123456-regional-dispatch.opus",
                "Ogg Opus, 8 kHz mono")
        ];

        public string CallHistoryFilterText { get; set; } = string.Empty;
        public IReadOnlyList<string> RecordingDirectionFilters => All;
        public IReadOnlyList<string> RecordingProtocolFilters => All;
        public IReadOnlyList<string> RecordingEncryptionFilters => All;
        public string RecordingDirectionFilter { get; set; } = "All";
        public string RecordingProtocolFilter { get; set; } = "All";
        public string RecordingEncryptionFilter { get; set; } = "All";
        public string RecordingSystemFilterText { get; set; } = string.Empty;
        public string RecordingChannelFilterText { get; set; } = string.Empty;
        public string RecordingTalkgroupFilterText { get; set; } = string.Empty;
        public string RecordingSubscriberFilterText { get; set; } = string.Empty;
        public string RecordingAliasFilterText { get; set; } = string.Empty;
        public DateTimeOffset? RecordingStartDateFilter { get; set; }
        public DateTimeOffset? RecordingEndDateFilter { get; set; }
        public string HistoryFilterSummary => "1 item";
        public System.Collections.IEnumerable FilteredCallHistory => entries;
    }

    private sealed record TestCallHistoryItem(
        string TimestampText,
        string DateText,
        string DisplayChannelText,
        string SystemName,
        string DirectionText,
        string ProtocolText,
        string DurationText,
        string RouteText,
        string EncryptionText,
        bool HasRecording,
        bool HasPlayableRecording,
        string RecordingFileName,
        string RecordingDetailsText) : ICallHistoryItemViewModel;

    private sealed class TestAudioSettingsViewModel : IAudioSettingsViewModel
    {
        private static readonly ICommand NoOp = new TestCommand();
        private static readonly IAudioDeviceOptionViewModel[] Devices =
        [
            new TestAudioDevice("System default device"),
            new TestAudioDevice("An exceptionally long Bluetooth operator headset name")
        ];
        private static readonly IRxAudioProcessingModeViewModel[] Modes =
        [
            new TestRxAudioProcessingMode("P25 Phase 1"),
            new TestRxAudioProcessingMode("DMR")
        ];
        private static readonly IAudioInputPresetViewModel[] Presets =
        [
            new TestAudioInputPreset("Dispatch microphone: gain 1.2, EQ +2/0/-1 dB")
        ];
        private static readonly IChannelAudioRouteSystemViewModel[] RouteSystems =
        [
            new TestAudioRouteSystem(
                "Regional Interoperability System",
                [
                    new TestAudioRouteChannel("County Fire Dispatch", Devices),
                    new TestAudioRouteChannel(
                        "An exceptionally long mutual-aid dispatch channel name",
                        Devices)
                ])
        ];

        public System.Collections.IEnumerable AudioInputDevices => Devices;
        public IAudioDeviceOptionViewModel? SelectedAudioInputDevice { get; set; } = Devices[0];
        public System.Collections.IEnumerable AudioOutputDevices => Devices;
        public IAudioDeviceOptionViewModel? SelectedAudioOutputDevice { get; set; } = Devices[1];
        public ICommand RefreshAudioDevicesCommand => NoOp;
        public bool IsMicrophonePermissionRequestAvailable => true;
        public System.Collections.IEnumerable RxAudioProcessingModes => Modes;
        public ICommand ApplyRxAudioProcessingOptionsCommand => NoOp;
        public bool IsAppleVoiceProcessingPlatformAvailable => false;
        public IReadOnlyList<string> AudioProcessingModeOptions => ["DVM Console processing", "Apple Voice Processing"];
        public string SelectedAudioProcessingMode { get; set; } = "DVM Console processing";
        public string AudioProcessingDescription => "Portable microphone processing with operator-controlled gain and equalization.";
        public bool IsDvmConsoleProcessingSelected => true;
        public string AudioInputGainText { get; set; } = "1.2";
        public string AudioInputLowGainText { get; set; } = "2";
        public string AudioInputMidGainText { get; set; } = "0";
        public string AudioInputHighGainText { get; set; } = "-1";
        public bool AudioInputAgcEnabled { get; set; } = true;
        public bool IsAgcTargetEnabled => true;
        public string AudioInputAgcTargetDbfsText { get; set; } = "-25";
        public bool KeepTransmitMicrophoneWarm { get; set; }
        public ICommand ApplyAudioInputSettingsCommand => NoOp;
        public string AudioInputPresetNameText { get; set; } = "Dispatch microphone";
        public System.Collections.IEnumerable AudioInputPresets => Presets;
        public System.Collections.IEnumerable AudioRouteSystems => RouteSystems;
    }

    private sealed class TestGeneralSettingsViewModel : IGeneralSettingsViewModel
    {
        private static readonly IToolbarClockViewModel[] Clocks =
        [
            new TestToolbarClock("Clock 1", "UTC-08:00", Brushes.CornflowerBlue),
            new TestToolbarClock("Clock 2", "UTC+00:00", Brushes.Goldenrod)
        ];

        public string SettingsVersionText => "Settings schema 12";
        public string UiFontSizeText => $"Interface text size: {UiFontSize:0} pt";
        public double UiFontSize { get; set; } = 14;
        public string UiScaleText => $"Interface scale: {UiScale:0.00}";
        public double UiScale { get; set; } = 1;
        public bool TogglePttMode { get; set; }
        public bool TalkPermitTone { get; set; } = true;
        public bool ConnectionChimes { get; set; } = true;
        public bool LocalToneMonitorEnabled { get; set; }
        public bool VerboseLoggingEnabled { get; set; }
        public bool MuteRxAudioWhileTransmitting { get; set; } = true;
        public bool RestoreSelectedChannelsOnStartup { get; set; } = true;
        public bool RetainPatchStateOnStartup { get; set; } = true;
        public bool DarkMode { get; set; }
        public bool KeepWindowOnTop { get; set; }
        public bool ShowSystemStatus { get; set; } = true;
        public bool ShowChannels { get; set; } = true;
        public bool ShowAlertTones { get; set; } = true;
        public bool LockWidgets { get; set; }
        public System.Collections.IEnumerable ToolbarClocks => Clocks;
        public bool ClockUse24HourTime { get; set; }
        public bool ClockShowSeconds { get; set; } = true;
        public string GlobalPttKeyText => "Space";
        public string ActiveSystemPttKeyText => "Shift+Space";
    }

    private sealed class TestToolbarClock : IToolbarClockViewModel
    {
        private readonly IToolbarClockUtcOffsetOption[] _utcOptions;
        private readonly IToolbarClockColorOption[] _colorOptions;

        public TestToolbarClock(string slotLabel, string timeZoneLabel, IBrush color)
        {
            SlotLabel = slotLabel;
            TimeZoneLabel = timeZoneLabel;
            _utcOptions =
            [
                new TestToolbarClockUtcOffset("UTC-08:00"),
                new TestToolbarClockUtcOffset("UTC+00:00")
            ];
            _colorOptions =
            [
                new TestToolbarClockColor("Primary", color),
                new TestToolbarClockColor("Neutral", Brushes.Gray)
            ];
            SelectedUtcOffsetOption = _utcOptions[0];
            SelectedColorOption = _colorOptions[0];
        }

        public bool Enabled { get; set; } = true;
        public string SlotLabel { get; }
        public System.Collections.IEnumerable UtcOffsetOptions => _utcOptions;
        public IToolbarClockUtcOffsetOption? SelectedUtcOffsetOption { get; set; }
        public System.Collections.IEnumerable ColorOptions => _colorOptions;
        public IToolbarClockColorOption SelectedColorOption { get; set; }
        public string TimeZoneLabel { get; }
    }

    private sealed record TestToolbarClockUtcOffset(string Label) : IToolbarClockUtcOffsetOption;

    private sealed record TestToolbarClockColor(string Label, IBrush ColorBrush) : IToolbarClockColorOption;

    private sealed class TestWebStreamsSettingsViewModel : IWebStreamsSettingsViewModel
    {
        private static readonly IWebStreamViewModel[] Streams =
        [
            new TestWebStream("County fire dispatch web stream", "Receiving"),
            new TestWebStream(
                "An exceptionally long regional interoperability audio stream name",
                "Waiting for the remote server to reconnect")
        ];

        public System.Collections.IEnumerable WebStreams => Streams;
    }

    private sealed class TestWebStream : IWebStreamViewModel
    {
        private static readonly IAudioDeviceOptionViewModel[] Devices =
        [
            new TestAudioDevice("System default device"),
            new TestAudioDevice("An exceptionally long Bluetooth operator headset name")
        ];

        public TestWebStream(string name, string statusText)
        {
            Name = name;
            StatusText = statusText;
        }

        public string Name { get; }
        public string StatusText { get; }
        public string ToggleButtonText => "Start";
        public ICommand ToggleCommand { get; } = new TestCommand();
        public double Volume { get; set; } = 1;
        public System.Collections.IEnumerable OutputDeviceOptions => Devices;
        public IAudioDeviceOptionViewModel? SelectedOutputDevice { get; set; } = Devices[0];
    }

    private sealed class TestRecorderSettingsViewModel : IRecorderSettingsViewModel
    {
        private static readonly IRecorderSystemViewModel[] Systems =
        [
            new TestRecorderSystem(
                "Regional Interoperability System",
                [
                    new TestRecorderChannel("County Fire Dispatch", "Regional / TGID 3001"),
                    new TestRecorderChannel(
                        "An exceptionally long mutual-aid dispatch channel name",
                        "Regional / TGID 3002")
                ])
        ];

        public TestRecorderSettingsViewModel(bool externalLocationAvailable)
        {
            IsExternalRecordingLocationAvailable = externalLocationAvailable;
        }

        public bool IsExternalRecordingLocationAvailable { get; }
        public string RecordingLocationText { get; set; } = "/example/operator recordings";
        public string RecordingRetentionDaysText { get; set; } = "30";
        public ICommand ApplyRecordingRetentionCommand { get; } = new TestCommand();
        public System.Collections.IEnumerable RecorderSystems => Systems;
    }

    private sealed record TestRecorderSystem(
        string Name,
        IReadOnlyList<IRecorderChannelViewModel> Channels) : IRecorderSystemViewModel
    {
        public System.Collections.IEnumerable RecorderChannels => Channels;
    }

    private sealed class TestRecorderChannel(
        string name,
        string destinationText) : IRecorderChannelViewModel
    {
        public string Name { get; } = name;
        public string DestinationText { get; } = destinationText;
        public string RecordingConfigurationButtonText => "Enable TAR";
        public IBrush RecordingSelectionBrush => Brushes.Transparent;
        public IBrush RecordingSelectionBorderBrush => Brushes.Gray;
        public ICommand RecordingCommand { get; } = new TestCommand();
        public string IgnoredSubscriberIdsText { get; set; } = "1001, 1002";
    }

    private sealed class TestPttSettingsViewModel : IPttSettingsViewModel
    {
        private static readonly string[] KeyOptions = ["None", "F3", "F4"];
        private static readonly string[] PortOptions = ["/dev/cu.operator", "COM4"];
        private static readonly int[] BaudRates = [9_600, 19_200];

        public TestPttSettingsViewModel(bool keyboardAvailable, bool serialAvailable)
        {
            IsKeyboardPttCapabilityAvailable = keyboardAvailable;
            IsSerialPttCapabilityAvailable = serialAvailable;
        }

        public bool HasHardwarePttCapabilities =>
            IsKeyboardPttCapabilityAvailable || IsSerialPttCapabilityAvailable;
        public bool IsKeyboardPttCapabilityAvailable { get; }
        public bool IsKeyboardPermissionRequestAvailable => false;
        public System.Collections.IEnumerable KeyboardPttKeyOptions => KeyOptions;
        public string SelectedGlobalPttKeyName { get; set; } = "F3";
        public string SelectedActiveSystemPttKeyName { get; set; } = "F4";
        public bool TogglePttMode { get; set; }
        public bool IsSerialPttCapabilityAvailable { get; }
        public bool SerialPttEnabled { get; set; } = true;
        public bool SerialPttActiveSystemOnly { get; set; }
        public System.Collections.IEnumerable SerialPttPortOptions => PortOptions;
        public string SerialPttPortName { get; set; } = PortOptions[0];
        public System.Collections.IEnumerable SerialPttBaudRates => BaudRates;
        public int SerialPttBaudRate { get; set; } = BaudRates[0];
        public string SerialPttStatusText => "Serial PTT ready.";
    }

    private sealed class TestToneSettingsViewModel : IToneSettingsViewModel
    {
        private static readonly IDtmfPresetViewModel[] DtmfPresetItems =
        [
            new TestDtmfPreset("Regional paging access: 1/0.25s 2/0.25s hold/0.5s")
        ];
        private static readonly IToneSequenceStepViewModel[] ToneSteps =
        [
            new TestToneSequenceStep(false, "1000", "1"),
            new TestToneSequenceStep(true, "0", "0.5")
        ];
        private static readonly ITonePresetViewModel[] TonePresetItems =
        [
            new TestTonePreset(
                "Station alert",
                "Station alert: 1000Hz/1s hold/0.5s 800Hz/3s")
        ];
        private static readonly IAlertToneViewModel[] AlertItems =
        [
            new TestAlertTone(
                "Evacuation announcement",
                "Evacuation announcement — evacuation-message.opus",
                "Managed asset · evacuation-message.opus")
        ];

        public string DtmfDigits { get; set; } = "123#";
        public string DtmfPresetName { get; set; } = "Regional paging";
        public ICommand SendDtmfCommand { get; } = new TestCommand();
        public ICommand SaveDtmfPresetCommand { get; } = new TestCommand();
        public System.Collections.IEnumerable DtmfPresets => DtmfPresetItems;
        public System.Collections.IEnumerable ToneSequenceSteps => ToneSteps;
        public string TonePresetName { get; set; } = "Station alert";
        public ICommand SendToneCommand { get; } = new TestCommand();
        public ICommand SaveTonePresetCommand { get; } = new TestCommand();
        public System.Collections.IEnumerable TonePresets => TonePresetItems;
        public string QuickCallToneAText { get; set; } = "600";
        public string QuickCallToneBText { get; set; } = "900";
        public string AlertToneNameText { get; set; } = "Evacuation announcement";
        public System.Collections.IEnumerable AlertTones => AlertItems;
    }

    private sealed record TestDtmfPreset(string DisplayText) : IDtmfPresetViewModel;

    private sealed record TestTonePreset(string Name, string DisplayText) : ITonePresetViewModel;

    private sealed class TestToneSequenceStep(
        bool isSilence,
        string frequencyText,
        string durationText) : IToneSequenceStepViewModel
    {
        public bool IsSilence { get; set; } = isSilence;
        public string FrequencyText { get; set; } = frequencyText;
        public string DurationText { get; set; } = durationText;
    }

    private sealed record TestAlertTone(
        string Name,
        string DisplayText,
        string StorageText) : IAlertToneViewModel;

    private sealed class TestGroupSettingsViewModel : IGroupSettingsViewModel
    {
        private readonly PatchGroupEditorViewModel[] groups;

        public TestGroupSettingsViewModel()
        {
            TestPatchChannel[] channels =
            [
                new("Regional", "County Fire Dispatch", 3001, canListen: true, canTransmit: true),
                new("Regional", "An exceptionally long mutual-aid receive-only channel name", 3002, canListen: true, canTransmit: false),
                new("Regional", "City Operations", 3003, canListen: true, canTransmit: true)
            ];
            PatchMemberEditorViewModel[] patchMembers = channels
                .Select(channel => new PatchMemberEditorViewModel(channel, member: true))
                .ToArray();
            var patch = new PatchGroupEditorViewModel(
                "One-way regional mutual aid",
                enabled: true,
                oneWay: true,
                patchMembers,
                oneWaySourceKey: patchMembers[1].RoutingKey);
            var multiSelect = new PatchGroupEditorViewModel(
                "Dispatch multi-select",
                enabled: true,
                oneWay: false,
                channels.Select(channel => new PatchMemberEditorViewModel(channel, member: true)),
                isMultiSelect: true);
            groups = [patch, multiSelect];
        }

        public System.Collections.IEnumerable PatchGroups => groups;
    }

    private sealed class TestPatchChannel : IPatchMemberChannelViewModel
    {
        public TestPatchChannel(
            string systemName,
            string name,
            uint destinationId,
            bool canListen,
            bool canTransmit)
        {
            SystemName = systemName;
            Name = name;
            DestinationId = destinationId;
            CanListen = canListen;
            CanTransmit = canTransmit;
            RoutingKey = $"{systemName}:{destinationId}";
            SettingsKey = $"{systemName}\u001F{name}";
            Id = new ChannelId(new ChannelSessionId(
                systemName,
                ChannelProtocol.P25,
                destinationId,
                0,
                RoutingKey));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public ChannelId Id { get; }
        public string RoutingKey { get; }
        public string SettingsKey { get; }
        public string SystemName { get; }
        public string Name { get; }
        public string ModeText => "P25";
        public uint DestinationId { get; }
        public bool CanListen { get; }
        public bool CanTransmit { get; }
    }

    private sealed class TestConnectionsSettingsViewModel : IConnectionsSettingsViewModel
    {
        private static readonly IConnectionSystemViewModel[] Systems =
        [
            new TestConnectionSystem("Regional Interoperability", "fne.example.invalid:62031"),
            new TestConnectionSystem("County Public Safety", "county.example.invalid:62031"),
            new TestConnectionSystem("Municipal Services", "city.example.invalid:62031")
        ];
        private static readonly IKeyStatusItemViewModel[] Keys =
        [
            new TestKeyStatus("Regional Interoperability", "County Fire Dispatch", "P25", "0x84", "0x1234", "Available · FNE/KMM", string.Empty),
            new TestKeyStatus("Regional Interoperability", "An exceptionally long mutual-aid dispatch channel name", "DMR", "0x04", "0x2A", "Key unavailable", "Local entry: protocol: dmr · algId: 0x04 · key: 16 bytes"),
            new TestKeyStatus("County Public Safety", "Sheriff Dispatch", "P25", "0x81", "0x0020", "Available · local file", string.Empty),
            new TestKeyStatus("Municipal Services", "Public Works", "NXDN", "0x01", "0x03", "Key unavailable", "Select a key in Configuration Studio")
        ];

        public System.Collections.IEnumerable ConnectionSystems => Systems;
        public System.Collections.IEnumerable KeyStatusItems => Keys;
    }

    private sealed class TestConnectionSystem : IConnectionSystemViewModel
    {
        private readonly IRxJitterBufferModeViewModel[] modes =
        [
            new TestJitterMode("P25 Phase 1"),
            new TestJitterMode("DMR"),
            new TestJitterMode("NXDN")
        ];

        public TestConnectionSystem(string name, string endpoint)
        {
            Name = name;
            Endpoint = endpoint;
        }

        public string Name { get; }
        public string Endpoint { get; }
        public System.Collections.IEnumerable RxJitterBufferModes => modes;
        public string ConnectionButtonText => "Connect";
        public string AdaptiveJitterLearnedText => "Adaptive jitter learned P25 60 ms · DMR 80 ms · NXDN 40 ms";
        public string JitterBufferEffectivenessText => "Late packets 0.2% · concealed frames 0.1%";
        public string ConnectionStatus => "Disconnected";
        public string TrafficTotalsText => "RX 123,456 · TX 1,234";
        public string StreamTrafficText => "Active streams: none; previous stream 0x12345678";
        public string ConnectionHealthText => "Local receive health · no discarded control traffic or UI backlog drops";
    }

    private sealed class TestJitterMode : IRxJitterBufferModeViewModel
    {
        private readonly IRxJitterBufferOptionViewModel[] options =
        [
            new TestJitterOption("Off (lowest latency)"),
            new TestJitterOption("60 ms (3 packets)"),
            new TestJitterOption("Adaptive ≤ 200 ms")
        ];

        public TestJitterMode(string modeName)
        {
            ModeName = modeName;
            SelectedOption = options[2];
        }

        public string ModeName { get; }
        public System.Collections.IEnumerable Options => options;
        public IRxJitterBufferOptionViewModel SelectedOption { get; set; }
    }

    private sealed record TestJitterOption(string Label) : IRxJitterBufferOptionViewModel;

    private sealed record TestKeyStatus(
        string SystemName,
        string ChannelName,
        string ModeText,
        string AlgorithmIdText,
        string KeyIdText,
        string StatusText,
        string ConfigurationHint) : IKeyStatusItemViewModel
    {
        public bool HasConfigurationHint => ConfigurationHint.Length > 0;
    }

    private sealed record TestAudioDevice(string DisplayName) : IAudioDeviceOptionViewModel;

    private sealed record TestAudioInputPreset(string DisplayText) : IAudioInputPresetViewModel;

    private sealed record TestAudioRouteSystem(
        string Name,
        IReadOnlyList<IChannelAudioRouteViewModel> Channels) : IChannelAudioRouteSystemViewModel
    {
        public System.Collections.IEnumerable AudioRouteChannels => Channels;
    }

    private sealed class TestAudioRouteChannel(
        string name,
        IReadOnlyList<IAudioDeviceOptionViewModel> devices) : IChannelAudioRouteViewModel
    {
        public string Name { get; } = name;
        public System.Collections.IEnumerable OutputDeviceOptions { get; } = devices;
        public IAudioDeviceOptionViewModel? SelectedOutputDevice { get; set; } = devices[0];
        public double StereoBalance { get; set; }
        public string StereoBalanceText => "Center";
    }

    private sealed class TestRxAudioProcessingMode(string modeName) : IRxAudioProcessingModeViewModel
    {
        public string ModeName { get; } = modeName;
        public bool HighPassFilterEnabled { get; set; } = true;
        public decimal HighPassFrequencyHz { get; set; } = 250;
        public bool PeakingFilterEnabled { get; set; } = true;
        public decimal PeakingFrequencyHz { get; set; } = 2500;
        public decimal PeakingGainDb { get; set; } = 3;
        public bool CompressorEnabled { get; set; }
        public decimal CompressorRatio { get; set; } = 3;
        public decimal CompressorThresholdDbfs { get; set; } = -18;
        public decimal CompressorMakeupGainDb { get; set; } = 3;
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
        }
    }
}
