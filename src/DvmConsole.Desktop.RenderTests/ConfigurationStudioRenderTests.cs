using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using System.Text.Json;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DvmConsole.Desktop.RenderTests.HeadlessTestApp))]

namespace DvmConsole.Desktop.RenderTests;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

public sealed class ConfigurationStudioRenderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [AvaloniaFact]
    public void ConfigurationStudioHierarchyWheelScrollsTheWholeNavigationSidebar()
    {
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);
        ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Zones);

        try
        {
            mainWindow.Show();
            for (int index = 0; index < 18; index++)
                studio.StudioViewModel.DuplicateZone();

            studio.Show(mainWindow);
            studio.UpdateLayout();

            ConfigurationStudioNavigationView navigation = studio
                .FindControl<ConfigurationStudioView>("studioView")!
                .FindControl<ConfigurationStudioNavigationView>("Navigation")!;
            ScrollViewer scroller = navigation.FindControl<ScrollViewer>("NavigationScroller")!;
            TreeView hierarchy = navigation.FindControl<TreeView>("ConfigurationTree")!;
            Button groups = navigation.FindControl<Button>("GroupsNavigationButton")!;
            Button encryptionKeys = navigation.FindControl<Button>("EncryptionKeysNavigationButton")!;
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetVerticalScrollBarVisibility(hierarchy));

            Point wheelPoint = hierarchy.TranslatePoint(
                new Point(hierarchy.Bounds.Width / 2, Math.Min(100, hierarchy.Bounds.Height / 2)),
                studio)!.Value;
            for (int index = 0; index < 40; index++)
                studio.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.None);
            studio.UpdateLayout();

            Assert.True(scroller.Offset.Y > 0);
            Assert.True(IsWithinViewport(groups, scroller));
            Assert.True(IsWithinViewport(encryptionKeys, scroller));
        }
        finally
        {
            studio.CloseForSessionReplacement();
            mainWindow.Close();
        }
    }

    [AvaloniaFact]
    public void LegacyCodeplugImportRestoresPrePhaseOneChannelCardCoordinates()
    {
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        const string channelKey = "North Metro\u001FNorth Dispatch";
        settingsStore.Save(new UserSettings
        {
            LastCodeplugPath = demoCodeplug,
            ChannelWidgetPositions = new Dictionary<string, WidgetPositionSetting>
            {
                [channelKey] = new WidgetPositionSetting { X = 417, Y = 233 }
            }
        });
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);

        try
        {
            mainWindow.Show();
            var viewModel = Assert.IsType<MainWindowViewModel>(mainWindow.DataContext);
            ChannelViewModel channel = viewModel.Zones
                .SelectMany(zone => zone.Channels)
                .Single(candidate => candidate.SettingsKey == channelKey);

            Assert.Equal(417, channel.WidgetX);
            Assert.Equal(233, channel.WidgetY);
            Assert.NotNull(viewModel.ConfigurationReference);
        }
        finally
        {
            mainWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task NewConfigurationOwnsManagedDraftBeforeStudioOpensAndHonorsCancelDiscard()
    {
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);
        string libraryRoot = Path.Combine(
            Path.GetDirectoryName(demoState.UserSettingsPath)!,
            "ConfigurationLibrary");

        try
        {
            mainWindow.Show();
            var library = new ManagedConfigurationLibrary(libraryRoot);
            int catalogCountBefore = await CountAsync(library.ListAsync());

            await mainWindow.OpenConfigurationStudioAsync(
                ConfigurationStudioSection.Overview,
                createNew: true);
            ConfigurationStudioWindow studio = Assert.IsType<ConfigurationStudioWindow>(
                mainWindow.OpenConfigurationStudioWindow);
            Assert.True(studio.ManagedConfigurationId.HasValue);
            ConfigurationId id = studio.ManagedConfigurationId.Value;
            ConfigurationDraft draft = await library.OpenDraftAsync(id);

            Assert.True(draft.IsDirty);
            Assert.Null(draft.BasedOnRevision);
            Assert.DoesNotContain(await ReadAllAsync(library.ListAsync()), item => item.Id == id);
            Assert.Equal(catalogCountBefore, await CountAsync(library.ListAsync()));

            studio.DraftReplacementChoiceOverride = () =>
                Task.FromResult(ConfigurationDraftReplacementChoice.Cancel);
            Assert.False(await studio.ConfirmSessionReplacementAsync());
            Assert.Same(studio, mainWindow.OpenConfigurationStudioWindow);

            studio.DraftReplacementChoiceOverride = () =>
                Task.FromResult(ConfigurationDraftReplacementChoice.Discard);
            Assert.True(await studio.ConfirmSessionReplacementAsync());
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Null(mainWindow.OpenConfigurationStudioWindow);

            ConfigurationDraft replacement = await library.CreateDraftAsync("Replacement");
            Assert.NotEqual(id, replacement.Id);
            await library.DiscardDraftAsync(replacement.Id);
        }
        finally
        {
            mainWindow.OpenConfigurationStudioWindow?.CloseForSessionReplacement();
            mainWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task StudioWindowsRenderWithSanitizedDemoConfiguration()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("DVMCONSOLE_DOC_CAPTURE_DIR");
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);

        try
        {
            mainWindow.Show();
            ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
                ConfigurationStudioSection.Overview);
            studio.Show(mainWindow);
            studio.UpdateLayout();
            Assert.IsType<ConfigurationStudioViewModel>(studio.DataContext);
            Assert.False(studio.StudioViewModel.HasErrors);
            ConfigurationStudioView sharedStudio = studio.FindControl<ConfigurationStudioView>("studioView")!;
            Border footer = sharedStudio.FindControl<Border>("Footer")!;
            Grid footerGrid = sharedStudio.FindControl<Grid>("FooterGrid")!;
            StackPanel footerActions = sharedStudio.FindControl<StackPanel>("FooterActions")!;
            foreach (string automationName in new[]
                     {
                         "Save configuration copy",
                         "Export configuration YAML",
                         "Review and save configuration"
                     })
            {
                Button action = Assert.Single(
                    sharedStudio.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        automationName,
                        StringComparison.Ordinal));
                Assert.True(
                    action.IsEffectivelyVisible && action.Bounds.Width > 0 && action.Bounds.Height > 0,
                    $"Configuration Studio footer action '{automationName}' was not visible; " +
                    $"bounds={action.Bounds}; parent={action.GetVisualParent()?.Bounds}; " +
                    $"footer={sharedStudio.FindControl<Border>("Footer")?.Bounds}; " +
                    $"footer grid={sharedStudio.FindControl<Grid>("FooterGrid")?.Bounds}.");
                Point actionOrigin = action.TranslatePoint(default, footer) ?? default;
                Assert.True(
                    actionOrigin.Y >= 10 &&
                    actionOrigin.Y + action.Bounds.Height <= footer.Bounds.Height - 5,
                    $"Configuration Studio footer action '{automationName}' collided with its border; " +
                    $"origin={actionOrigin}; bounds={action.Bounds}; footer={footer.Bounds}; " +
                    $"grid={footerGrid.Bounds}, grid-desired={footerGrid.DesiredSize}, grid-origin={footerGrid.TranslatePoint(default, footer)}; " +
                    $"actions={footerActions.Bounds}, actions-origin={footerActions.TranslatePoint(default, footer)}; " +
                    $"children={string.Join("; ", footerActions.Children.OfType<Button>().Select(button => $"{button.Content}:{button.Bounds}"))}.");
            }
            await VerifyStudioNavigationAndChannelFiltering(studio);
            studio.CloseForSessionReplacement();

            ConfigurationStudioWindow menuStudio = mainWindow.CreateConfigurationStudioForCapture(
                ConfigurationStudioSection.Zones);
            menuStudio.Show(mainWindow);
            menuStudio.UpdateLayout();
            await VerifyEveryEditMenuCommand(menuStudio);
            menuStudio.CloseForSessionReplacement();

            VerifyUndoRedoCoversReferencedFilesLayoutAndEmptyZoneOwnership(
                mainWindow,
                settingsStore,
                demoCodeplug);

            if (!string.IsNullOrWhiteSpace(captureDirectory))
                await App.CaptureDemoScreenshotsCoreAsync(mainWindow, captureDirectory);

            VerifyReviewPlanMigratesOperatorState(mainWindow, settingsStore, demoCodeplug);
        }
        finally
        {
            mainWindow.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(880, false)]
    [InlineData(599, true)]
    [InlineData(390, false)]
    [InlineData(360, true)]
    public void SharedConfigurationStudioRendersAtMobileWidths(double width, bool dark)
    {
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);
        ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Overview);
        var host = new Window
        {
            Width = width,
            Height = 760,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };

        try
        {
            mainWindow.Show();
            ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
            var overview = new ConfigurationStudioOverviewView { DataContext = viewModel };
            host.Content = overview;
            host.Show();
            host.UpdateLayout();
            ScrollViewer overviewScroller = overview.FindControl<ScrollViewer>("OverviewScroller")!;
            Assert.True(
                overviewScroller.Extent.Width <= overviewScroller.Viewport.Width + 1,
                $"Studio overview extent {overviewScroller.Extent.Width} exceeded viewport {overviewScroller.Viewport.Width} at {width} logical pixels.");

            viewModel.SelectSection(ConfigurationStudioSection.Systems);
            var systems = new ConfigurationStudioSystemsView { DataContext = viewModel };
            host.Content = systems;
            host.UpdateLayout();
            AssertPortableStudioPage(systems, "SystemsScroller", "systems", width);

            viewModel.SelectSection(ConfigurationStudioSection.Streams);
            var streams = new ConfigurationStudioStreamsView { DataContext = viewModel };
            host.Content = streams;
            host.UpdateLayout();
            AssertPortableStudioPage(streams, "StreamsScroller", "streams", width);

            viewModel.SelectSection(ConfigurationStudioSection.Groups);
            var groups = new ConfigurationStudioGroupsView { DataContext = viewModel };
            host.Content = groups;
            host.UpdateLayout();
            AssertPortableStudioPage(groups, "GroupsScroller", "groups", width);

            viewModel.SelectSection(ConfigurationStudioSection.Zones);
            viewModel.IsZonePreviewExpanded = false;
            var zones = new ConfigurationStudioZonesView { DataContext = viewModel };
            host.Content = zones;
            host.UpdateLayout();
            AssertPortableZonesPage(zones, width);

            var shell = new ConfigurationStudioView { DataContext = viewModel };
            host.Content = shell;
            host.UpdateLayout();
            Grid shellLayout = shell.FindControl<Grid>("ShellLayout")!;
            Assert.True(
                shellLayout.DesiredSize.Width <= shell.Bounds.Width + 1,
                $"Studio shell desired width {shellLayout.DesiredSize.Width} exceeded {shell.Bounds.Width} at {width} logical pixels.");
            StackPanel footerActions = shell.FindControl<StackPanel>("FooterActions")!;
            Assert.True(
                footerActions.Bounds.Right <= shell.Bounds.Width + 1,
                $"Studio footer exceeded the {width}-pixel viewport.");
            Control[] visibleButtons = shell.GetVisualDescendants().OfType<Button>()
                .Where(button => button.GetType() == typeof(Button) && button.IsEffectivelyVisible)
                .ToArray();
            Assert.NotEmpty(visibleButtons);
            Assert.All(visibleButtons, button =>
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
            if (width < 400)
            {
                Assert.All(
                    visibleButtons.Where(button => button.IsHitTestVisible),
                    button => Assert.True(
                        button.Bounds.Height >= 44,
                        $"Studio shell button touch target was {button.Bounds.Height} pixels high."));
            }
        }
        finally
        {
            host.Close();
            studio.CloseForSessionReplacement();
            mainWindow.Close();
        }
    }

    private static void AssertPortableStudioPage(
        UserControl page,
        string scrollerName,
        string pageName,
        double width)
    {
        ScrollViewer scroller = page.FindControl<ScrollViewer>(scrollerName)!;
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width + 1,
            $"Studio {pageName} extent {scroller.Extent.Width} exceeded viewport {scroller.Viewport.Width} at {width} logical pixels.");
        Control[] interactive = page.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible)
            .Where(control => control.GetType() == typeof(Button)
                || control is CheckBox
                || control is ComboBox
                || control is TextBox
                || control is NumericUpDown)
            .Where(control => control is not TextBox
                || !control.GetVisualAncestors().OfType<NumericUpDown>().Any())
            .ToArray();
        Assert.NotEmpty(interactive);
        Assert.All(interactive, control =>
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
        if (width < 400)
        {
            Assert.All(interactive, control => Assert.True(
                control.Bounds.Height >= 44,
                $"{pageName} {control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
        }
    }

    private static void AssertPortableZonesPage(ConfigurationStudioZonesView page, double width)
    {
        Grid layout = page.FindControl<Grid>("ZoneLayout")!;
        Grid desktopTable = page.FindControl<Grid>("DesktopChannelTable")!;
        ListBox narrowList = page.FindControl<ListBox>("narrowChannelList")!;
        Assert.True(
            layout.DesiredSize.Width <= page.Bounds.Width + 1,
            $"Studio zones desired width {layout.DesiredSize.Width} exceeded {page.Bounds.Width} at {width} logical pixels.");
        Assert.Equal(width < 880, narrowList.IsVisible);
        Assert.Equal(width >= 880, desktopTable.IsVisible);

        Control[] interactive = page.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible)
            .Where(control => control is TextBox
                || control is ComboBox
                || control is NumericUpDown
                || control is CheckBox
                || control.GetType() == typeof(ToggleButton))
            .Where(control => control is not TextBox
                || !control.GetVisualAncestors().OfType<NumericUpDown>().Any())
            .Where(control => control.GetType() != typeof(ToggleButton)
                || !control.GetVisualAncestors().OfType<Expander>().Any())
            .ToArray();
        Assert.NotEmpty(interactive);
        Assert.All(interactive, control =>
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))));
        if (width < 400)
        {
            Assert.All(interactive.Where(control => control.IsHitTestVisible), control => Assert.True(
                control.Bounds.Height >= 44,
                $"zones {control.GetType().Name} touch target was {control.Bounds.Height} pixels high."));
        }
    }

    [AvaloniaFact]
    public async Task ChannelSelectionScrollsOnceAndStaysStableAboveTheLayoutDrawer()
    {
        string demoCodeplug = Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml");
        using DemoSessionState demoState = DemoSessionState.Create();
        var settingsStore = new UserSettingsStore(demoState.UserSettingsPath);
        var mainWindow = new MainWindow(
            demoCodeplug,
            settingsStore,
            new OperatorViewStore(demoState.OperatorViewPath),
            demoMode: true);

        try
        {
            mainWindow.Show();
            ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
                ConfigurationStudioSection.Zones);
            studio.Width = 1488;
            studio.Height = 760;
            studio.Show(mainWindow);

            ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
            for (int index = viewModel.VisibleChannelRows.Count; index < 18; index++)
                viewModel.AddChannel();
            viewModel.IsZonePreviewExpanded = true;
            studio.UpdateLayout();

            ConfigurationStudioZonesView zones = studio.FindControl<ConfigurationStudioView>("studioView")!.ZonesView;
            ListBox channelList = zones.FindControl<ListBox>("channelList")!;
            Assert.False(channelList.AutoScrollToSelectedItem);
            Assert.False(ScrollViewer.GetBringIntoViewOnFocusChange(channelList));
            viewModel.SelectedChannelRow = viewModel.VisibleChannelRows[^2];
            await FlushSelectionCommitAsync();
            studio.UpdateLayout();

            ScrollViewer channelScrollViewer = Assert.Single(
                channelList.GetVisualDescendants().OfType<ScrollViewer>());
            double selectedOffset = channelScrollViewer.Offset.Y;
            Assert.True(selectedOffset > 0);

            for (int pass = 0; pass < 6; pass++)
            {
                studio.UpdateLayout();
                await FlushSelectionCommitAsync();
                Assert.Equal(selectedOffset, channelScrollViewer.Offset.Y, precision: 3);
            }

            Border layoutDrawer = zones.FindControl<Border>("liveZoneLayoutDrawer")!;
            Assert.True(channelList.Bounds.Bottom <= layoutDrawer.Bounds.Top + 0.5);
            studio.CloseForSessionReplacement();
        }
        finally
        {
            mainWindow.Close();
        }
    }

    private static void VerifyUndoRedoCoversReferencedFilesLayoutAndEmptyZoneOwnership(
        MainWindow mainWindow,
        UserSettingsStore settingsStore,
        string demoCodeplug)
    {
        ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Zones);
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;

        IConfigurationChannelPreviewViewModel preview = viewModel.PreviewChannels[0];
        double originalX = preview.X;
        double originalY = preview.Y;
        viewModel.BeginPreviewMove();
        viewModel.MovePreviewChannel(preview, originalX + 50, originalY + 60);
        viewModel.CommitPreviewMove();
        Assert.True(viewModel.CanUndo);
        viewModel.Undo();
        Assert.Equal(originalX, viewModel.PreviewChannels[0].X);
        Assert.Equal(originalY, viewModel.PreviewChannels[0].Y);
        viewModel.Redo();
        Assert.Equal(originalX + 50, viewModel.PreviewChannels[0].X);
        Assert.Equal(originalY + 60, viewModel.PreviewChannels[0].Y);

        viewModel.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        ushort originalKeyId = viewModel.SelectedKey!.KeyId;
        viewModel.SelectedKeyIdHexDigits = "2A";
        viewModel.CommitKeyEdit();
        Assert.Equal(0x2A, viewModel.SelectedKey.KeyId);
        viewModel.Undo();
        Assert.Equal(originalKeyId, viewModel.SelectedKey!.KeyId);
        viewModel.Redo();
        Assert.Equal(0x2A, viewModel.SelectedKey!.KeyId);

        viewModel.SelectSection(ConfigurationStudioSection.Files);
        ConfigurationAliasRow alias = Assert.IsType<ConfigurationAliasRow>(viewModel.SelectedAlias);
        string originalAlias = alias.Alias.Alias;
        alias.Alias.Alias = "Undo test alias";
        viewModel.CommitAliasEdit();
        viewModel.Undo();
        Assert.Equal(originalAlias, viewModel.SelectedAlias!.Alias.Alias);
        viewModel.Redo();
        Assert.Equal("Undo test alias", viewModel.SelectedAlias!.Alias.Alias);

        viewModel.AddZone();
        ZoneConfiguration emptyZone = viewModel.SelectedZone!;
        Assert.Empty(emptyZone.Channels);
        string initialSystem = viewModel.SelectedZoneSystemName;
        string alternateSystem = Assert.Single(
            viewModel.Systems,
            system => !string.Equals(system.Name, initialSystem, StringComparison.OrdinalIgnoreCase)).Name;
        viewModel.SelectedZoneSystemName = alternateSystem;
        viewModel.CommitZoneSystemEdit();
        viewModel.Undo();
        Assert.Equal(initialSystem, viewModel.SelectedZoneSystemName);
        viewModel.Redo();
        Assert.Equal(alternateSystem, viewModel.SelectedZoneSystemName);

        var savePlanner = new DesktopConfigurationStudioSavePlanner(viewModel, settingsStore);
        ConfigurationSavePlan plan = savePlanner.CreatePlan(demoCodeplug);
        string json = plan.Files.Single(file => file.Category == "Operator settings").Content;
        UserSettings settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions)!;
        CodeplugStudioState studioState = CodeplugStudioStateStore.Get(settings, demoCodeplug);
        Assert.Equal(alternateSystem, studioState.ZoneSystemAssignments[emptyZone.Name]);
    }

    private static async Task VerifyEveryEditMenuCommand(ConfigurationStudioWindow studio)
    {
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        ConfigurationStudioZonesView zones = studio.FindControl<ConfigurationStudioView>("studioView")!.ZonesView;
        viewModel.SelectSection(ConfigurationStudioSection.Zones);
        ZoneConfiguration populatedZone = viewModel.SelectedZone!;
        int originalChannelCount = populatedZone.Channels.Count;
        int originalZoneCount = viewModel.Configuration.Zones.Count;

        MenuItem editMenu = zones.FindControl<MenuItem>("editMenuRoot")!;
        MenuItem[] commands = editMenu.Items.OfType<MenuItem>().ToArray();
        ConfigurationStudioEditCommand[] wiredCommands = commands
            .Select(item => Assert.IsType<string>(item.Tag))
            .Select(tag => Enum.Parse<ConfigurationStudioEditCommand>(tag))
            .ToArray();
        Assert.Equal(
            Enum.GetValues<ConfigurationStudioEditCommand>().OrderBy(value => value),
            wiredCommands.OrderBy(value => value));

        static Task<bool> ConfirmDelete(string _, string __, string ___) => Task.FromResult(true);
        studio.EditMenuConfirmationOverride = ConfirmDelete;
        void Click(ConfigurationStudioEditCommand command)
        {
            MenuItem item = Assert.Single(commands, candidate =>
                string.Equals(candidate.Tag as string, command.ToString(), StringComparison.Ordinal));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        Click(ConfigurationStudioEditCommand.AddChannel);
        Assert.Equal(originalChannelCount + 1, populatedZone.Channels.Count);
        Assert.Equal(populatedZone.Channels.Count, viewModel.PreviewChannels.Count);

        Click(ConfigurationStudioEditCommand.DuplicateChannel);
        Assert.Equal(originalChannelCount + 2, populatedZone.Channels.Count);
        Assert.Equal(populatedZone.Channels.Count, viewModel.PreviewChannels.Count);

        ChannelConfiguration movedChannel = viewModel.SelectedChannel!;
        int originalIndex = populatedZone.Channels.IndexOf(movedChannel);
        Click(ConfigurationStudioEditCommand.MoveChannelUp);
        Assert.Equal(originalIndex - 1, populatedZone.Channels.IndexOf(movedChannel));
        Click(ConfigurationStudioEditCommand.MoveChannelDown);
        Assert.Equal(originalIndex, populatedZone.Channels.IndexOf(movedChannel));

        ListBox channelList = zones.FindControl<ListBox>("channelList")!;
        channelList.SelectAll();
        viewModel.SelectedChannel!.CardSize = "large";
        Click(ConfigurationStudioEditCommand.ApplySelectedCardSize);
        ChannelConfiguration[] selectedChannels = channelList.SelectedItems!
            .OfType<ConfigurationChannelRow>()
            .Select(row => row.Channel)
            .ToArray();
        Assert.NotEmpty(selectedChannels);
        Assert.All(selectedChannels, channel => Assert.Equal("large", channel.CardSize));

        Click(ConfigurationStudioEditCommand.SetSelectedRowsRxOnly);
        Assert.All(selectedChannels, channel => Assert.True(channel.RxOnly));
        Click(ConfigurationStudioEditCommand.SetSelectedRowsTxCapable);
        Assert.All(selectedChannels, channel => Assert.False(channel.RxOnly));

        Click(ConfigurationStudioEditCommand.DeleteChannel);
        Assert.Equal(originalChannelCount + 1, populatedZone.Channels.Count);
        Assert.Equal(populatedZone.Channels.Count, viewModel.PreviewChannels.Count);

        Click(ConfigurationStudioEditCommand.AddZone);
        Assert.Equal(originalZoneCount + 1, viewModel.Configuration.Zones.Count);
        Assert.Empty(viewModel.SelectedZone!.Channels);
        Click(ConfigurationStudioEditCommand.DeleteZone);
        Assert.Equal(originalZoneCount, viewModel.Configuration.Zones.Count);

        viewModel.SelectedZone = populatedZone;
        Click(ConfigurationStudioEditCommand.DuplicateZone);
        Assert.Equal(originalZoneCount + 1, viewModel.Configuration.Zones.Count);
        Assert.Equal(populatedZone.Channels.Count, viewModel.SelectedZone!.Channels.Count);
        Assert.Equal(viewModel.SelectedZone.Channels.Count, viewModel.PreviewChannels.Count);
        Click(ConfigurationStudioEditCommand.DeleteZone);
        Assert.Equal(originalZoneCount, viewModel.Configuration.Zones.Count);
        studio.EditMenuConfirmationOverride = null;
        await Task.CompletedTask;
    }

    private static async Task VerifyStudioNavigationAndChannelFiltering(ConfigurationStudioWindow studio)
    {
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        ConfigurationStudioZonesView zones = studio.FindControl<ConfigurationStudioView>("studioView")!.ZonesView;
        viewModel.SelectSection(ConfigurationStudioSection.Zones);
        Assert.Equal(viewModel.Systems.Count, viewModel.ConfigurationHierarchy.Count);
        Assert.All(viewModel.ConfigurationHierarchy, systemNode =>
        {
            Assert.True(systemNode.IsSystem);
            Assert.NotNull(systemNode.System);
            Assert.All(systemNode.Children, zoneNode =>
            {
                Assert.True(zoneNode.IsZone);
                Assert.NotNull(zoneNode.Zone);
                Assert.All(zoneNode.Children, channelNode =>
                {
                    Assert.True(channelNode.IsChannel);
                    Assert.Same(zoneNode.Zone, channelNode.Zone);
                });
            });
        });
        Assert.NotEmpty(viewModel.VisibleChannelRows);
        ConfigurationChannelRow p25Row = Assert.Single(
            viewModel.VisibleChannelRows,
            row => row.Channel.Mode == "p25");
        viewModel.SelectedChannelRow = p25Row;
        ConfigurationHierarchyNode selectedZoneNode = Assert.Single(
            viewModel.ConfigurationHierarchy.SelectMany(systemNode => systemNode.Children),
            zoneNode => ReferenceEquals(zoneNode.Zone, viewModel.SelectedZone));
        ConfigurationHierarchyNode selectedSystemNode = Assert.Single(
            viewModel.ConfigurationHierarchy,
            systemNode => systemNode.Children.Contains(selectedZoneNode));
        Assert.True(selectedSystemNode.IsExpanded);
        Assert.True(selectedZoneNode.IsExpanded);
        Assert.Same(p25Row.Channel, viewModel.SelectedHierarchyNode!.Channel);
        Assert.Equal("P25 Phase 1", p25Row.ModeText);
        Assert.Contains("P25 Phase 1", p25Row.DestinationText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, p25Row.SlotText);
        Assert.Contains("P25 Phase 1", viewModel.PreviewChannels
            .Single(preview => ReferenceEquals(preview.Channel, p25Row.Channel))
            .TalkgroupText, StringComparison.Ordinal);
        Assert.Contains(viewModel.ModeOptions, option => option.Value == "p25" && option.DisplayName == "P25 Phase 1");
        Assert.Contains(viewModel.ProtocolOptions, option => option.Value == "p25" && option.DisplayName == "P25 Phase 1");
        Assert.Contains("mode: p25", viewModel.Document.Serialize(), StringComparison.Ordinal);

        NumericUpDown slotEditor = zones.FindControl<NumericUpDown>("dmrSlotEditor")!;
        StackPanel slotSettings = zones.FindControl<StackPanel>("dmrSlotSettings")!;
        ListBox channelList = zones.FindControl<ListBox>("channelList")!;
        Border layoutDrawer = zones.FindControl<Border>("liveZoneLayoutDrawer")!;
        Assert.False(viewModel.IsSelectedChannelDmr);
        Assert.False(slotSettings.IsVisible);
        Assert.Equal("0", slotEditor.FormatString);
        Assert.Equal(1m, slotEditor.Increment);
        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(channelList));
        studio.UpdateLayout();
        Assert.True(channelList.Bounds.Height > 0);
        Assert.True(channelList.Bounds.Bottom <= layoutDrawer.Bounds.Top + 0.5);
        Assert.True(viewModel.PreviewCanvasWidth >= MainWindowViewModel.DefaultWidgetCanvasWidth);
        Assert.All(viewModel.PreviewChannels, preview =>
        {
            Assert.Equal(preview.Card.CardWidth, preview.CardWidth);
            Assert.Equal(viewModel.PreviewCardHeight, preview.CardHeight);
        });

        IConfigurationChannelPreviewViewModel firstPreview = viewModel.PreviewChannels[0];
        viewModel.MovePreviewChannel(firstPreview, 40, 150);
        Assert.Equal(40, firstPreview.X);
        Assert.Equal(150, firstPreview.Y);
        Assert.True(viewModel.LayoutChanged);

        EncryptionAlgorithmOption p25Aes = Assert.Single(
            viewModel.AvailableChannelAlgorithms,
            option => option.ConfigurationValue == "aes");
        viewModel.SelectedChannelAlgorithm = p25Aes;
        viewModel.SelectedChannelKeyIdHexDigits = "50";
        viewModel.CommitChannelAlgorithmEdit();
        Assert.Equal("aes", viewModel.SelectedChannel!.Algo);
        Assert.Equal("0x50", viewModel.SelectedChannel.KeyId);

        ComboBox modeEditor = zones.FindControl<ComboBox>("channelModeEditor")!;
        modeEditor.SelectedValue = "dmr";
        await FlushSelectionCommitAsync();
        studio.UpdateLayout();
        Assert.True(viewModel.IsSelectedChannelDmr);
        Assert.True(slotSettings.IsVisible);
        Assert.Equal("dmr", viewModel.SelectedChannel.Mode);

        modeEditor.SelectedValue = "p25";
        await FlushSelectionCommitAsync();
        studio.UpdateLayout();
        Assert.False(viewModel.IsSelectedChannelDmr);
        Assert.False(slotSettings.IsVisible);

        ZoneConfiguration assignedZone = viewModel.SelectedZone!;
        string originalSystem = viewModel.SelectedZoneSystemName;
        SystemConfiguration alternateSystem = Assert.Single(viewModel.Systems, system =>
            !string.Equals(system.Name, originalSystem, StringComparison.OrdinalIgnoreCase));
        ComboBox zoneSystemEditor = zones.FindControl<ComboBox>("zoneSystemEditor")!;
        zoneSystemEditor.SelectedValue = alternateSystem.Name;
        await FlushSelectionCommitAsync();
        Assert.Equal(alternateSystem.Name, viewModel.SelectedZoneSystemName);
        Assert.All(assignedZone.Channels, channel => Assert.Equal(alternateSystem.Name, channel.System));
        Assert.Contains(
            viewModel.ConfigurationHierarchy.Single(node => ReferenceEquals(node.System, alternateSystem)).Children,
            node => ReferenceEquals(node.Zone, assignedZone));
        zoneSystemEditor.SelectedValue = originalSystem;
        await FlushSelectionCommitAsync();

        viewModel.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        Assert.Contains(viewModel.AvailableKeyAlgorithms, option =>
            option.DisplayName == "AES-256" && option.AlgorithmId == 0x84);
        Assert.Equal("0x1", viewModel.SelectedKey!.KeyIdText);
        Assert.Equal("P25 Phase 1", viewModel.SelectedKey.ProtocolDisplayName);
        viewModel.SelectedKeyProtocol = "dmr";
        Assert.Equal("dmr", viewModel.SelectedKeyProtocol);
        viewModel.CommitKeyProtocolEdit();
        Assert.Contains(viewModel.AvailableKeyAlgorithms, option => option.AlgorithmId == 0x05);
        Assert.Equal(0x05, viewModel.SelectedKey.AlgId);
        Assert.Equal("0x05", viewModel.SelectedKeyAlgorithmIdText);

        viewModel.ChannelSearchText = "North Dispatch";
        ConfigurationChannelRow row = Assert.Single(viewModel.VisibleChannelRows);
        Assert.Equal("North Dispatch", row.Name);
        viewModel.ChannelSearchText = "no matching channel";
        Assert.Empty(viewModel.VisibleChannelRows);
        viewModel.ChannelSearchText = string.Empty;

        viewModel.IsZonePreviewExpanded = false;
        Assert.False(viewModel.IsZonePreviewExpanded);
        viewModel.IsZonePreviewExpanded = true;

        viewModel.SelectedSystem!.Address = string.Empty;
        viewModel.CommitFieldEdit();
        ConfigurationValidationIssue issue = Assert.Single(
            viewModel.ValidationIssues,
            item => item.Path == "systems[0].address");
        viewModel.OpenValidationDrawer();
        Assert.True(viewModel.IsValidationDrawerOpen);
        viewModel.NavigateToIssue(issue);
        Assert.True(viewModel.IsSystems);
        Assert.Same(viewModel.Systems[0], viewModel.SelectedSystem);
    }

    private static async Task FlushSelectionCommitAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static bool IsWithinViewport(Control control, ScrollViewer scroller)
    {
        Point? origin = control.TranslatePoint(default, scroller);
        return origin is { } point &&
               point.Y >= 0 &&
               point.Y + control.Bounds.Height <= scroller.Viewport.Height + 0.5;
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (T value in source)
            values.Add(value);
        return values;
    }

    private static async Task<int> CountAsync<T>(IAsyncEnumerable<T> source)
        => (await ReadAllAsync(source)).Count;

    private static void VerifyReviewPlanMigratesOperatorState(
        MainWindow mainWindow,
        UserSettingsStore settingsStore,
        string demoCodeplug)
    {
        string activeCodeplug = ((MainWindowViewModel)mainWindow.DataContext!).CurrentCodeplugPath
            ?? throw new InvalidOperationException("The managed demo configuration was not loaded.");
        UserSettings settings = settingsStore.Load();
        settings.LastSelectedSystemName = "North Metro";
        settings.RxJitterBuffersBySystem["North Metro"] = new RxJitterBufferSetting();
        settings.ChannelVolumes["North Metro\u001FNorth Dispatch"] = 0.5;
        CodeplugGroupState state = CodeplugGroupStateStore.GetOrMigrate(settings, activeCodeplug);
        state.Memberships["Shared Operations"] =
        [
            new PatchMemberSetting
            {
                SystemName = "North Metro",
                DestinationId = 3101,
                ChannelName = "North Dispatch"
            }
        ];
        settingsStore.Save(settings);

        ConfigurationStudioWindow studio = mainWindow.CreateConfigurationStudioForCapture(
            ConfigurationStudioSection.Overview);
        ConfigurationStudioViewModel viewModel = studio.StudioViewModel;
        viewModel.Configuration.Systems[0].Name = "North Regional";
        viewModel.Configuration.Zones[0].Channels[0].Name = "Regional Dispatch";
        viewModel.Configuration.Groups[0].Name = "Regional Operations";
        viewModel.CommitFieldEdit();

        var savePlanner = new DesktopConfigurationStudioSavePlanner(viewModel, settingsStore);
        ConfigurationSavePlan plan = savePlanner.CreatePlan(activeCodeplug);
        string json = plan.Files.Single(file => file.Category == "Operator settings").Content;
        UserSettings migrated = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions)!;
        CodeplugGroupState migratedGroupState = CodeplugGroupStateStore.GetOrMigrate(migrated, activeCodeplug);

        Assert.Equal("North Regional", migrated.LastSelectedSystemName);
        Assert.True(migrated.RxJitterBuffersBySystem.ContainsKey("North Regional"));
        Assert.Equal(0.5, migrated.ChannelVolumes["North Regional\u001FRegional Dispatch"]);
        Assert.True(migratedGroupState.Memberships.ContainsKey("Regional Operations"));
        Assert.Equal("North Regional", migratedGroupState.Memberships["Regional Operations"][0].SystemName);
        Assert.Equal("Regional Dispatch", migratedGroupState.Memberships["Regional Operations"][0].ChannelName);
        string review = savePlanner.BuildReviewText(plan);
        Assert.Contains("System state: North Metro → North Regional", review, StringComparison.Ordinal);
        Assert.Contains("Channel state: North Metro/North Dispatch → North Regional/Regional Dispatch", review, StringComparison.Ordinal);
        Assert.Contains("Group state: Shared Operations → Regional Operations", review, StringComparison.Ordinal);

        string saveAsDirectory = Path.Combine(Path.GetTempPath(), "dvmconsole-studio-saveas-tests", Guid.NewGuid().ToString("N"));
        string saveAsPath = Path.Combine(saveAsDirectory, "codeplug.yml");
        ConfigurationSavePlan saveAsPlan = savePlanner.CreatePlan(saveAsPath);
        Assert.True(saveAsPlan.CanSave);
        Assert.Contains(saveAsPlan.Files, file =>
            file.Category == "Encryption key file" &&
            file.Path == Path.Combine(saveAsDirectory, "keys.clear"));
        Assert.Contains(saveAsPlan.Files, file =>
            file.Category == "RID alias file" &&
            file.Path == Path.Combine(saveAsDirectory, "aliases.yml"));
        string saveAsJson = saveAsPlan.Files.Single(file => file.Category == "Operator settings").Content;
        UserSettings saveAsSettings = JsonSerializer.Deserialize<UserSettings>(saveAsJson, JsonOptions)!;
        CodeplugGroupState copiedState = CodeplugGroupStateStore.GetOrMigrate(saveAsSettings, saveAsPath);
        Assert.True(
            copiedState.Memberships.ContainsKey("Regional Operations"),
            $"Expected Save As membership migration. Found: {string.Join(", ", copiedState.Memberships.Keys)}");
        CodeplugStudioState sourceStudioState = CodeplugStudioStateStore.Get(saveAsSettings, activeCodeplug);
        CodeplugStudioState copiedStudioState = CodeplugStudioStateStore.Get(saveAsSettings, saveAsPath);
        Assert.NotSame(sourceStudioState, copiedStudioState);
        Assert.Empty(sourceStudioState.ZoneSystemAssignments);
        Assert.NotEmpty(copiedStudioState.ZoneSystemAssignments);
    }
}
