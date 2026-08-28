using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using System.Collections.Specialized;
using System.Reflection;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly MainWindowSessionHost sessionHost;
    private readonly WindowPttKeyRouter pttKeyRouter;
    private readonly OperatorCommandCatalog operatorCommandCatalog;
    private readonly UserSettingsStore sessionUserSettingsStore;
    private readonly OperatorViewStore operatorViewStore;
    private readonly LatestOperatorViewWriter operatorViewWriter;
    private readonly OperatorViewSettings operatorViewSettings;
    private readonly EngineeringHealthViewModel engineeringHealthViewModel;
    private readonly bool demoMode;
    private MainWindowViewModel viewModel => sessionHost.ViewModel;
    private CardPttController cardPtt => sessionHost.CardPtt;
    private OperatorToolsWindow? operatorToolsWindow;
    private ConfigurationStudioWindow? configurationStudioWindow;
    private DebugLogWindow? debugLogWindow;
    private DocumentationWindow? documentationWindow;
    private AboutWindow? aboutWindow;
    private readonly List<DispatcherTimer> scrollBarTimers = [];
    private readonly HashSet<ScrollViewer> configuredScrollViewers = [];
    private readonly ScrollViewportAnchor<CallHistoryEntry> activityViewportAnchor;
    private readonly MainWindowPlacementController mainWindowPlacement;
    private Control? draggedChannelCard;
    private ChannelViewModel? draggedChannel;
    private Point dragPointerOrigin;
    private double dragWidgetXOrigin;
    private double dragWidgetYOrigin;
    private bool draggedChannelMoved;
    private bool toggleReceiveAfterChannelClick;
    private int shutdownStarted;
    private bool shutdownComplete;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? configurationPath)
        : this(
            configurationPath,
            new UserSettingsStore(UserSettingsStore.DefaultPath),
            new OperatorViewStore(OperatorViewStore.DefaultPath),
            demoMode: false)
    {
    }

    internal MainWindow(
        string? configurationPath,
        UserSettingsStore sessionUserSettingsStore,
        OperatorViewStore operatorViewStore,
        bool demoMode)
    {
        this.sessionUserSettingsStore = sessionUserSettingsStore ??
            throw new ArgumentNullException(nameof(sessionUserSettingsStore));
        this.operatorViewStore = operatorViewStore ??
            throw new ArgumentNullException(nameof(operatorViewStore));
        this.demoMode = demoMode;
        InitializeComponent();
        PopulatePttKeyMenus();
        // Avalonia can leave named controls declared inside nested MenuItems
        // unresolved when the compiled XAML is loaded from a published
        // self-contained apphost. Resolve them from the window name scope
        // before the startup menu refreshes run.
        recentCodeplugsMenu ??= this.FindControl<MenuItem>("recentCodeplugsMenu");
        namedSettingsProfileLoadMenu ??= this.FindControl<MenuItem>("namedSettingsProfileLoadMenu");
        namedSettingsProfileDeleteMenu ??= this.FindControl<MenuItem>("namedSettingsProfileDeleteMenu");
        toolbarClocks ??= this.FindControl<ItemsControl>("toolbarClocks")
            ?? throw new InvalidOperationException("The responsive toolbar clocks were not initialized.");
        toolbarAlertToneShortcuts ??= this.FindControl<ItemsControl>("toolbarAlertToneShortcuts")
            ?? throw new InvalidOperationException("The responsive alert shortcuts were not initialized.");
        toolbarTonesLauncher ??= this.FindControl<Button>("toolbarTonesLauncher")
            ?? throw new InvalidOperationException("The responsive tones launcher was not initialized.");
        toolbarOverflowMenu ??= this.FindControl<Menu>("toolbarOverflowMenu")
            ?? throw new InvalidOperationException("The responsive toolbar overflow was not initialized.");
        mainShellGrid ??= this.FindControl<Grid>("mainShellGrid")
            ?? throw new InvalidOperationException("The main shell grid was not initialized.");
        engineeringHealthMenuItem ??= this.FindControl<MenuItem>("engineeringHealthMenuItem")
            ?? throw new InvalidOperationException("The Engineering Health menu item was not initialized.");
        engineeringHealthSplitter ??= this.FindControl<GridSplitter>("engineeringHealthSplitter")
            ?? throw new InvalidOperationException("The Engineering Health splitter was not initialized.");
        engineeringHealthPane ??= this.FindControl<EngineeringHealthPane>("engineeringHealthPane")
            ?? throw new InvalidOperationException("The Engineering Health pane was not initialized.");
        activityCallHistoryList ??= this.FindControl<ItemsControl>("activityCallHistoryList")
            ?? throw new InvalidOperationException("The Activity history list was not initialized.");
        MainWindowViewModel initialViewModel = LoadSessionViewModel(configurationPath);
        operatorViewSettings = LoadOperatorViewSettings();
        operatorViewWriter = new LatestOperatorViewWriter(
            this.operatorViewStore.Save,
            exception => DesktopCrashLog.Write("Operator view persistence", exception));
        engineeringHealthViewModel = new EngineeringHealthViewModel(initialViewModel);
        engineeringHealthPane.DataContext = engineeringHealthViewModel;
        mainWindowPlacement = new MainWindowPlacementController(this, initialViewModel.MainWindowPlacement);
        mainWindowPlacement.PrepareSize();
        activityViewportAnchor = new ScrollViewportAnchor<CallHistoryEntry>(
            () => activityScrollViewer,
            () => activityCallHistoryList.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("activity-call-card")),
            control => control.DataContext as CallHistoryEntry);
        sessionHost = new MainWindowSessionHost(
            initialViewModel,
            HandleActivityHistoryCollectionChanging,
            ApplySessionDataContext,
            CloseModelessViewModelWindows,
            CloseAllModelessWindows);
        pttKeyRouter = new WindowPttKeyRouter(() => viewModel);
        operatorCommandCatalog = CreateOperatorCommandCatalog();
        ApplyEngineeringHealthVisibility();
        activityCallHistoryList.LayoutUpdated += HandleActivityHistoryLayoutUpdated;
        AddHandler(InputElement.KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel);
        AddHandler(InputElement.GotFocusEvent, HandlePttFocusChanged, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.LostFocusEvent, HandlePttFocusChanged, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerPressedEvent, HandlePttPointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerReleasedEvent, HandlePttPointerReleased, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerCaptureLostEvent, HandlePttPointerCaptureLost, RoutingStrategies.Bubble, true);
        RefreshRecentCodeplugMenu();
        RefreshNamedSettingsProfileMenus();
        Opened += async (_, _) =>
        {
            RefreshResponsiveToolbarVisibility(Bounds.Width);
            mainWindowPlacement.RestorePosition();
            mainWindowPlacement.StartTracking();
            ConfigureTransientChannelScrollBars();
            ConfigureTransientScrollBars(activityScrollViewer);
            await sessionHost.StartAsync().ConfigureAwait(false);
        };
        LayoutUpdated += (_, _) => ConfigureTransientChannelScrollBars();
        Closing += HandleClosing;
        Activated += (_, _) => UpdatePttFocusSuppression();
        Deactivated += async (_, _) =>
        {
            pttKeyRouter.UpdateInputFocus(null);
            await viewModel.FlushUserSettingsAsync().ConfigureAwait(false);
        };
    }

    private MainWindowViewModel LoadSessionViewModel(string? configurationPath)
    {
        MainWindowViewModel loaded = MainWindowViewModel.Load(
            configurationPath,
            sessionUserSettingsStore,
            networkDisabledDemo: demoMode);
        if (demoMode)
            loaded.InitializeDemoScenario();
        return loaded;
    }

    private OperatorViewSettings LoadOperatorViewSettings()
    {
        try
        {
            return operatorViewStore.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DesktopCrashLog.Write("Operator view preferences", exception);
            return new OperatorViewSettings();
        }
    }

    private void ApplySessionDataContext(MainWindowViewModel replacement)
    {
        DataContext = replacement;
        engineeringHealthViewModel.ReplaceConsole(replacement);
    }

    private void SetEngineeringHealthVisible(bool visible, bool persist = true)
    {
        if (!visible)
            CaptureEngineeringHealthHeight();
        operatorViewSettings.EngineeringHealthVisible = visible;
        ApplyEngineeringHealthVisibility();
        if (persist)
            ScheduleOperatorViewSave();
    }

    private void ApplyEngineeringHealthVisibility()
    {
        bool visible = operatorViewSettings.EngineeringHealthVisible;
        engineeringHealthPane.IsVisible = visible;
        engineeringHealthSplitter.IsVisible = visible;
        engineeringHealthMenuItem.IsChecked = visible;
        mainShellGrid.RowDefinitions[2].Height = visible ? new GridLength(5) : new GridLength(0);
        mainShellGrid.RowDefinitions[3].Height = visible
            ? new GridLength(operatorViewSettings.EngineeringHealthHeight)
            : new GridLength(0);
        engineeringHealthViewModel.SetActive(visible);
    }

    private void HandleEngineeringHealthSplitterPointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        CaptureEngineeringHealthHeight();
        ScheduleOperatorViewSave();
    }

    private void CaptureEngineeringHealthHeight()
    {
        if (!operatorViewSettings.EngineeringHealthVisible)
            return;
        double height = mainShellGrid.RowDefinitions[3].ActualHeight;
        if (!double.IsFinite(height) || height <= 0)
            return;
        operatorViewSettings.EngineeringHealthHeight = Math.Clamp(
            height,
            OperatorViewSettings.MinimumEngineeringHealthHeight,
            OperatorViewSettings.MaximumEngineeringHealthHeight);
    }

    internal void PrepareDemoCapture(
        double width,
        double height,
        bool showEngineeringHealth)
    {
        if (!demoMode)
            throw new InvalidOperationException("Screenshot capture is available only in the isolated demo session.");

        WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        RefreshResponsiveToolbarVisibility(Width);
        SetEngineeringHealthVisible(showEngineeringHealth, persist: false);
    }

    private void ScheduleOperatorViewSave()
        => operatorViewWriter.Schedule(operatorViewSettings.Snapshot());

    private OperatorCommandCatalog CreateOperatorCommandCatalog()
    {
        static Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        OperatorCommandDefinition OpenSection(OperatorToolSectionDefinition definition)
            => new(definition.CommandId, () => Run(() => OpenOperatorTools(definition.Section)));

        return new OperatorCommandCatalog(
        [
            new(
                OperatorCommandIds.Connect,
                () => Run(() => viewModel.ConnectCommand.Execute(null)),
                () => viewModel.ConnectCommand.CanExecute(null)),
            new(
                OperatorCommandIds.Disconnect,
                () => Run(() => viewModel.DisconnectCommand.Execute(null)),
                () => viewModel.DisconnectCommand.CanExecute(null)),
            new(
                OperatorCommandIds.EnableAllReceive,
                viewModel.EnableAllReceiveAsync),
            new(
                OperatorCommandIds.DisableAllReceive,
                viewModel.DisableAllReceiveAsync),
            new(
                OperatorCommandIds.EnableZoneReceive,
                viewModel.EnableSelectedZoneReceiveAsync,
                () => viewModel.HasSelectedZone),
            new(
                OperatorCommandIds.DisableZoneReceive,
                viewModel.DisableSelectedZoneReceiveAsync,
                () => viewModel.HasSelectedZone),
            new(
                OperatorCommandIds.ToggleAllTransmit,
                () => Run(viewModel.ToggleAllTransmitSelection)),
            new(
                OperatorCommandIds.SubscriberPage,
                () => OpenSubscriberCommandAsync(P25SubscriberCommand.CallAlert)),
            new(
                OperatorCommandIds.SubscriberRadioCheck,
                () => OpenSubscriberCommandAsync(P25SubscriberCommand.RadioCheck)),
            new(
                OperatorCommandIds.SubscriberInhibit,
                () => OpenSubscriberCommandAsync(P25SubscriberCommand.Inhibit)),
            new(
                OperatorCommandIds.SubscriberUninhibit,
                () => OpenSubscriberCommandAsync(P25SubscriberCommand.Uninhibit)),
            .. OperatorToolSectionCatalog.All.Select(OpenSection),
            new(
                OperatorCommandIds.DebugLogs,
                () => Run(ShowDebugLogs)),
            new(
                OperatorCommandIds.ToggleEngineeringHealth,
                () => Run(() => SetEngineeringHealthVisible(
                    !operatorViewSettings.EngineeringHealthVisible))),
            new(
                OperatorCommandIds.Documentation,
                () => Run(ShowDocumentation)),
            new(
                OperatorCommandIds.About,
                () => Run(ShowAbout))
        ]);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (shutdownComplete)
            return;

        // Keep the native window and application lifetime alive until every
        // session-owned asynchronous resource has completed cleanup. A second
        // close request remains cancelled while the same operation is running.
        e.Cancel = true;
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
            return;

        try
        {
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Main window shutdown", exception);
        }
        finally
        {
            shutdownComplete = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private async Task ShutdownAsync()
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(() =>
            viewModel.SaveMainWindowPlacement(mainWindowPlacement.GetPlacementForPersistence()));
        cleanup.Run(mainWindowPlacement.Dispose);
        cleanup.Run(() =>
        {
            foreach (DispatcherTimer timer in scrollBarTimers)
                timer.Stop();
        });
        cleanup.Run(() =>
            activityCallHistoryList.LayoutUpdated -= HandleActivityHistoryLayoutUpdated);
        cleanup.Run(activityViewportAnchor.Reset);
        cleanup.Run(CaptureEngineeringHealthHeight);
        await cleanup.RunTaskAsync(() => engineeringHealthViewModel.DisposeAsync().AsTask());
        await cleanup.RunTaskAsync(() => operatorViewWriter.DisposeAsync().AsTask());
        await cleanup.RunTaskAsync(() => BoundedShutdown.RunAsync(
            () => sessionHost.DisposeAsync().AsTask(),
            ShutdownTimeout));
        cleanup.ThrowIfFailed();
    }

    private void HandleActivityHistoryCollectionChanging(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            activityViewportAnchor.Reset();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
            activityViewportAnchor.Capture();
    }

    private void HandleActivityHistoryLayoutUpdated(object? sender, EventArgs e)
        => activityViewportAnchor.Restore();

    private async void HandleChannelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            ChannelCardInput.IsInteractiveSource(e.Source, control))
            return;

        if (control.DataContext is ChannelViewModel channel &&
            DataContext is MainWindowViewModel viewModel)
        {
            PointerPointProperties properties = e.GetCurrentPoint(control).Properties;
            if ((properties.IsLeftButtonPressed || properties.IsRightButtonPressed) && !viewModel.LockWidgets)
            {
                draggedChannelCard = control;
                draggedChannel = channel;
                dragPointerOrigin = e.GetPosition(this);
                dragWidgetXOrigin = channel.WidgetX;
                dragWidgetYOrigin = channel.WidgetY;
                draggedChannelMoved = false;
                toggleReceiveAfterChannelClick = properties.IsLeftButtonPressed;
                e.Pointer.Capture(control);
                e.Handled = true;
                control.Focus();
                return;
            }

            if (!properties.IsLeftButtonPressed)
                return;

            await viewModel.ToggleChannelReceiveAsync(channel);
            control.Focus();
        }
    }

    private void HandleChannelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedChannelCard is null || draggedChannel is null ||
            !ReferenceEquals(sender, draggedChannelCard) ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double deltaX = current.X - dragPointerOrigin.X;
        double deltaY = current.Y - dragPointerOrigin.Y;
        if (!draggedChannelMoved && Math.Abs(deltaX) < 4 && Math.Abs(deltaY) < 4)
            return;

        draggedChannelMoved = true;
        const double gridSize = 10;
        double x = Math.Max(0, Math.Round((dragWidgetXOrigin + deltaX) / gridSize) * gridSize);
        double y = Math.Max(0, Math.Round((dragWidgetYOrigin + deltaY) / gridSize) * gridSize);
        viewModel.MoveChannelWidget(draggedChannel, x, y, persist: false);
        e.Handled = true;
    }

    private async void HandleChannelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedChannelCard is null || draggedChannel is null || !ReferenceEquals(sender, draggedChannelCard))
            return;

        ChannelViewModel channel = draggedChannel;
        bool moved = draggedChannelMoved;
        bool toggleReceive = toggleReceiveAfterChannelClick;
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (moved)
                viewModel.MoveChannelWidget(channel, channel.WidgetX, channel.WidgetY, persist: true);
            else if (toggleReceive)
                await viewModel.ToggleChannelReceiveAsync(channel);
        }
        e.Pointer.Capture(null);
        ClearChannelDrag();
        e.Handled = true;
    }

    private void HandleChannelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (draggedChannelCard is not null && ReferenceEquals(sender, draggedChannelCard))
            ClearChannelDrag();
    }

    private void ClearChannelDrag()
    {
        draggedChannelCard = null;
        draggedChannel = null;
        draggedChannelMoved = false;
        toggleReceiveAfterChannelClick = false;
    }

    private async void HandlePttPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is not ChannelViewModel channel ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (viewModel.TogglePttMode)
        {
            await cardPtt.ToggleAsync(channel);
        }
        else
        {
            e.Pointer.Capture(button);
            await cardPtt.PressAsync(channel);
        }
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Button? button = e.Pointer.Captured as Button ?? FindPttButton(e.Source);
        if (button?.DataContext is not ChannelViewModel channel || !button.Classes.Contains("ptt"))
            return;

        e.Handled = true;
        e.Pointer.Capture(null);
        await cardPtt.ReleaseAsync(channel);
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is ChannelViewModel channel)
            await cardPtt.ReleaseAsync(channel);
    }

    internal async Task HandleAccessibleChannelPttKeyDownAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (viewModel.TogglePttMode)
            await cardPtt.ToggleAsync(channel);
        else
            await cardPtt.PressAsync(channel);
    }

    internal async Task HandleAccessibleChannelPttKeyUpAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!viewModel.TogglePttMode)
            await cardPtt.ReleaseAsync(channel);
    }

    private static Button? FindPttButton(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button button && button.Classes.Contains("ptt"))
                return button;
        }
        return null;
    }

    private async void HandleOpenCodeplugClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            await ShowCodeplugErrorAsync("This platform did not provide an available file picker.");
            return;
        }

        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Codeplug",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Codeplug YAML")
                    {
                        Patterns = ["*.yml", "*.yaml"],
                        MimeTypes = ["application/yaml", "text/yaml", "text/x-yaml"],
                        AppleUniformTypeIdentifiers = ["public.yaml", "public.text"]
                    }
                ]
            });
        }
        catch (Exception exception)
        {
            await ShowCodeplugErrorAsync($"The codeplug picker could not be opened.\n\n{exception.Message}");
            return;
        }

        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowCodeplugErrorAsync("The selected codeplug does not have a local filesystem path.");
            return;
        }

        await OpenCodeplugAsync(path);
    }

    private async void HandleOpenRecentCodeplugClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
            await OpenCodeplugAsync(path);
    }

    private async Task OpenCodeplugAsync(string path)
    {
        if (configurationStudioWindow is { } studio &&
            !await studio.ConfirmSessionReplacementAsync())
            return;
        configurationStudioWindow = null;

        MainWindowViewModel replacement;
        try
        {
            await sessionHost.PrepareForReplacementAsync();
            replacement = LoadSessionViewModel(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowCodeplugErrorAsync(
                $"Operator settings could not be saved before loading the codeplug.\n\n{exception.Message}");
            return;
        }

        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            await ShowCodeplugErrorAsync(error);
            return;
        }

        await ReplaceViewModelAsync(replacement);
    }

    private async void HandleNewConfigurationClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: true);

    private async void HandleConfigurationStudioClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: false);

    private async void HandleConfigurationGroupsClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Groups, createNew: false);

    private async Task OpenConfigurationStudioAsync(
        ConfigurationStudioSection section,
        bool createNew)
    {
        if (configurationStudioWindow is not null)
        {
            configurationStudioWindow.SelectSection(section);
            return;
        }

        await viewModel.FlushUserSettingsAsync();
        ConfigurationDocument document;
        try
        {
            string? path = viewModel.CurrentCodeplugPath;
            document = createNew || string.IsNullOrWhiteSpace(path)
                ? ConfigurationDocument.CreateNew()
                : ConfigurationDocument.Open(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            await ShowInformationAsync("Unable to open Configuration Studio", exception.Message);
            return;
        }

        configurationStudioWindow = new ConfigurationStudioWindow(
            document,
            viewModel,
            sessionUserSettingsStore,
            section);
        AttachPttInputSafety(configurationStudioWindow);
        configurationStudioWindow.ReloadRequested += OpenCodeplugAsync;
        configurationStudioWindow.Closed += (_, _) => configurationStudioWindow = null;
        configurationStudioWindow.Show(this);
    }

    internal ConfigurationStudioWindow CreateConfigurationStudioForCapture(
        ConfigurationStudioSection section)
    {
        string path = viewModel.CurrentCodeplugPath
            ?? throw new InvalidOperationException("A loaded demo codeplug is required for Studio capture.");
        return new ConfigurationStudioWindow(
            ConfigurationDocument.Open(path),
            viewModel,
            sessionUserSettingsStore,
            section)
        {
            Width = 1380,
            Height = 850
        };
    }

    private void RefreshRecentCodeplugMenu()
        => MainWindowMenuBuilder.ReplaceRecentCodeplugItems(
            recentCodeplugsMenu,
            viewModel.RecentCodeplugPaths,
            "No recent codeplugs",
            HandleOpenRecentCodeplugClick);

    private void RefreshNamedSettingsProfileMenus()
    {
        RefreshNamedSettingsProfileMenu(
            namedSettingsProfileLoadMenu,
            "No saved profiles",
            HandleLoadNamedSettingsProfileClick);
        RefreshNamedSettingsProfileMenu(
            namedSettingsProfileDeleteMenu,
            "No saved profiles",
            HandleDeleteNamedSettingsProfileClick);
    }

    private void RefreshNamedSettingsProfileMenu(
        MenuItem menu,
        string emptyHeader,
        EventHandler<RoutedEventArgs> clickHandler)
        => MainWindowMenuBuilder.ReplaceItems(menu, viewModel.NamedSettingsProfiles, emptyHeader, clickHandler);

    private async void HandleSelectBackgroundClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select user background",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/webp"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.SetUserBackground(path);
    }

    private void HandleClearBackgroundClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearUserBackground();

    private void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
        => viewModel.ResetLayout();

    private async void HandleImportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            await ShowInformationAsync("Import settings", "This platform did not provide an available file picker.");
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DVM Console Settings",
            AllowMultiple = false,
            FileTypeFilter = [SettingsFileType]
        });
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            await ShowInformationAsync("Import settings", "The selected settings file does not have a local filesystem path.");
            return;
        }

        try
        {
            string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
            await sessionHost.PrepareForReplacementAsync();
            viewModel.ImportSettings(path);
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath));
            await ShowInformationAsync("Settings imported", "The imported profile has been applied to the current console.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to import settings", exception.Message);
        }
    }

    private async void HandleSaveSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        string? profileName = await PromptForTextAsync(
            "Save settings profile",
            "Enter a name for the current operator settings profile.",
            "Save");
        if (profileName is null)
            return;

        try
        {
            viewModel.SaveNamedSettingsProfile(profileName);
            RefreshNamedSettingsProfileMenus();
            await ShowInformationAsync("Settings profile saved", $"Saved profile '{profileName}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to save settings profile", exception.Message);
        }
    }

    private async void HandleLoadNamedSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string profileName })
            return;

        SettingsImportPreview preview;
        try
        {
            preview = viewModel.PreviewNamedSettingsProfile(profileName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to preview settings profile", exception.Message);
            RefreshNamedSettingsProfileMenus();
            return;
        }

        if (!await ConfirmAsync(
                "Load settings profile",
                $"{preview.SummaryText}\n\nApply operator settings from '{profileName}'? The active codeplug and current channel selection will remain unchanged.",
                "Apply"))
        {
            return;
        }

        string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
        try
        {
            await sessionHost.PrepareForReplacementAsync();
            viewModel.ImportNamedSettingsProfile(profileName, SettingsImportScope.OperatorState);
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath));
            await ShowInformationAsync("Settings profile loaded", $"Applied operator settings from '{profileName}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to load settings profile", exception.Message);
            RefreshNamedSettingsProfileMenus();
        }
    }

    private async void HandleDeleteNamedSettingsProfileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string profileName } ||
            !await ConfirmAsync(
                "Delete settings profile",
                $"Delete the saved operator settings profile '{profileName}'?",
                "Delete"))
        {
            return;
        }

        try
        {
            viewModel.DeleteNamedSettingsProfile(profileName);
            RefreshNamedSettingsProfileMenus();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to delete settings profile", exception.Message);
        }
    }

    private async void HandleExportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            await ShowInformationAsync("Export settings", "This platform did not provide an available save picker.");
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DVM Console Settings",
            SuggestedFileName = "dvmconsole-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [SettingsFileType]
        });
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            viewModel.ExportSettings(path);
            await ShowInformationAsync("Settings exported", $"Settings were exported to:\n{path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to export settings", exception.Message);
        }
    }

    private async void HandleResetSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "Reset settings",
                "Reset all operator settings, presets, routes, selections, and layout preferences? The active codeplug itself will not be changed."))
        {
            return;
        }

        string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
        try
        {
            await sessionHost.PrepareForReplacementAsync();
            viewModel.ResetSettings();
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to reset settings", exception.Message);
        }
    }

    private async Task ReplaceViewModelAsync(MainWindowViewModel replacement)
    {
        await sessionHost.ReplaceAsync(replacement);
        RefreshRecentCodeplugMenu();
        RefreshNamedSettingsProfileMenus();
    }

    private void CloseModelessViewModelWindows()
    {
        ConfigurationStudioWindow? studio = configurationStudioWindow;
        configurationStudioWindow = null;
        studio?.CloseForSessionReplacement();

        OperatorToolsWindow? tools = operatorToolsWindow;
        operatorToolsWindow = null;
        tools?.Close();

        DebugLogWindow? logs = debugLogWindow;
        debugLogWindow = null;
        logs?.Close();

    }

    private void CloseAllModelessWindows()
    {
        CloseModelessViewModelWindows();

        DocumentationWindow? documentation = documentationWindow;
        documentationWindow = null;
        documentation?.Close();

        AboutWindow? about = aboutWindow;
        aboutWindow = null;
        about?.Close();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Reset")
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        AttachPttInputSafety(parts.Window);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) => { confirmed = true; parts.Window.Close(); };
        await parts.Window.ShowDialog(this);
        return confirmed;
    }

    private async Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateTextPrompt(title, message, confirmLabel, "Profile name");
        AttachPttInputSafety(parts.Window);
        TextBox input = parts.Input!;
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                confirmed = true;
                parts.Window.Close();
            }
        };
        parts.Window.Opened += (_, _) => input.Focus();
        await parts.Window.ShowDialog(this);
        return confirmed ? input.Text?.Trim() : null;
    }

    private static FilePickerFileType SettingsFileType { get; } = new("DVM Console Settings")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json", "text/json"],
        AppleUniformTypeIdentifiers = ["public.json"]
    };

    private async Task ShowCodeplugErrorAsync(string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage("Unable to open codeplug", message, "OK");
        AttachPttInputSafety(parts.Window);
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private async Task OpenSubscriberCommandAsync(P25SubscriberCommand command)
    {
        var window = new SubscriberCommandWindow(viewModel, command);
        AttachPttInputSafety(window);
        await window.ShowDialog(this);
    }

    private void ShowDebugLogs()
    {
        if (debugLogWindow is null)
        {
            debugLogWindow = new DebugLogWindow(viewModel);
            AttachPttInputSafety(debugLogWindow);
            debugLogWindow.Closed += (_, _) => debugLogWindow = null;
        }

        if (!debugLogWindow.IsVisible)
            debugLogWindow.Show();
        debugLogWindow.Activate();
    }

    private void HandleActivityDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source &&
            (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
        {
            e.Handled = true;
            return;
        }

        OpenOperatorTools(OperatorToolSection.History);
        e.Handled = true;
    }

    private void HandleCallHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source &&
            (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }

        if (sender is Border
            {
                DataContext: CallHistoryEntry
                {
                    HasPlayableRecording: true,
                    Recording: { } recording
                }
            })
        {
            viewModel.OpenRecording(recording);
            e.Handled = true;
        }
    }

    private void HandleToggleActivitySidebarClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ShowCallHistoryPane = !viewModel.ShowCallHistoryPane;
        e.Handled = true;
    }

    private void HandleToggleActivityZoneFilterClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ToggleActivityZoneFilter();
        e.Handled = true;
    }

    private void HandleToggleActivityReceiveFilterClick(object? sender, RoutedEventArgs e)
    {
        viewModel.ToggleActivityReceiveFilter();
        e.Handled = true;
    }

    internal void OpenOperatorTools(OperatorToolSection section)
    {
        if (operatorToolsWindow is null)
        {
            operatorToolsWindow = new OperatorToolsWindow(viewModel, section, pttKeyRouter);
            operatorToolsWindow.Closed += (_, _) => operatorToolsWindow = null;
            operatorToolsWindow.Show();
            return;
        }

        operatorToolsWindow.SelectSection(section);
        operatorToolsWindow.Activate();
    }

    private void ShowDocumentation()
    {
        if (documentationWindow is null)
        {
            documentationWindow = new DocumentationWindow();
            AttachPttInputSafety(documentationWindow);
            documentationWindow.Closed += (_, _) => documentationWindow = null;
        }

        if (!documentationWindow.IsVisible)
            documentationWindow.Show(this);
        documentationWindow.Activate();
    }

    private void ShowAbout()
    {
        if (aboutWindow is null)
        {
            aboutWindow = new AboutWindow();
            AttachPttInputSafety(aboutWindow);
            aboutWindow.Closed += (_, _) => aboutWindow = null;
        }

        if (!aboutWindow.IsVisible)
            aboutWindow.Show(this);
        aboutWindow.Activate();
    }

    internal static string ApplicationVersion =>
        typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unversioned development build";

    internal static string ShortApplicationVersion => FormatShortVersion(ApplicationVersion);

    internal static string FormatShortVersion(string informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "unversioned development build";

        string value = informationalVersion.Trim();
        int plusIndex = value.IndexOf('+');
        if (plusIndex < 0)
            return value;

        string version = value[..plusIndex];
        string revision = value[(plusIndex + 1)..].Split('.')[0];
        if (revision.Length == 0)
            return version;
        return $"{version} ({revision[..Math.Min(7, revision.Length)]})";
    }

    private async Task ShowInformationAsync(string title, string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage(title, message, "OK");
        AttachPttInputSafety(parts.Window);
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private void AttachPttInputSafety(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        void Refresh()
            => pttKeyRouter.UpdateInputFocus(window.FocusManager?.GetFocusedElement());

        window.AddHandler(
            InputElement.GotFocusEvent,
            (_, _) => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Input),
            RoutingStrategies.Bubble,
            true);
        window.AddHandler(
            InputElement.LostFocusEvent,
            (_, _) => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Input),
            RoutingStrategies.Bubble,
            true);
        window.Activated += (_, _) => Refresh();
        window.Deactivated += (_, _) => pttKeyRouter.UpdateInputFocus(null);
    }

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelTransmitSelection(channel);
    }

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelPageSelection(channel);
    }

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelViewModel channel })
            viewModel.ToggleChannelAlertSelection(channel);
    }

    private async void HandleSystemStatusClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await viewModel.ToggleSystemConnectionAsync(system);
    }

    private void HandleDismissCodeplugDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        viewModel.DismissCodeplugDiagnostics();
    }

    private async void HandleToolbarAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BuiltInAlertToneViewModel tone })
            await viewModel.SendBuiltInAlertToneAsync(tone);
    }

    private async void HandleOperatorCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string commandId })
            return;

        await operatorCommandCatalog.ExecuteAsync(commandId).ConfigureAwait(true);
    }

    private async void HandlePlayCallHistoryRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallHistoryEntry entry })
            await viewModel.ToggleCallHistoryRecordingPlaybackAsync(entry);
    }

    private async void HandleGlobalPttKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
            return;
        await viewModel.SetGlobalPttKeyAsync(key);
    }

    private async void HandleActiveSystemPttKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
            return;
        await viewModel.SetActiveSystemPttKeyAsync(key);
    }

    private void HandleExitClick(object? sender, RoutedEventArgs e) => Close();

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (pttKeyRouter.TryHandleKeyDown(e.Key, out bool handled))
            e.Handled = handled;
    }

    private void HandleKeyUp(object? sender, KeyEventArgs e)
    {
        if (pttKeyRouter.TryHandleKeyUp(e.Key, out bool handled))
            e.Handled = handled;
    }

    private void HandlePttFocusChanged(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(UpdatePttFocusSuppression, DispatcherPriority.Input);

    private void UpdatePttFocusSuppression()
        => pttKeyRouter.UpdateInputFocus(FocusManager?.GetFocusedElement());

    internal static bool TryMapPttKey(Key key, out KeyboardPttKey pttKey)
        => WindowPttKeyRouter.TryMap(key, out pttKey);

    private void ConfigureTransientScrollBars(ScrollViewer? viewer)
    {
        if (viewer is null || !configuredScrollViewers.Add(viewer))
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetScrollBarOpacity(viewer, 0);
        };
        viewer.ScrollChanged += (_, _) =>
        {
            SetScrollBarOpacity(viewer, 1);
            timer.Stop();
            timer.Start();
        };
        scrollBarTimers.Add(timer);
        Dispatcher.UIThread.Post(
            () => SetScrollBarOpacity(viewer, 0),
            DispatcherPriority.Loaded);
    }

    private void ConfigureTransientChannelScrollBars()
    {
        foreach (ScrollViewer viewer in this.GetVisualDescendants()
                     .OfType<ScrollViewer>()
                     .Where(viewer => viewer.Name == "channelScrollViewer"))
        {
            ConfigureTransientScrollBars(viewer);
        }
    }

    private static void SetScrollBarOpacity(ScrollViewer viewer, double opacity)
    {
        foreach (ScrollBar scrollBar in viewer.GetVisualDescendants().OfType<ScrollBar>())
            scrollBar.Opacity = opacity;
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private void PopulatePttKeyMenus()
    {
        MenuItem globalMenu = this.FindControl<MenuItem>("globalPttKeyMenu")
            ?? throw new InvalidOperationException("The global PTT key menu was not initialized.");
        MenuItem activeSystemMenu = this.FindControl<MenuItem>("activeSystemPttKeyMenu")
            ?? throw new InvalidOperationException("The active-system PTT key menu was not initialized.");
        MainWindowMenuBuilder.ReplacePttKeyItems(
            globalMenu,
            "None (keyboard PTT disabled)",
            HandleGlobalPttKeyClick);
        MainWindowMenuBuilder.ReplacePttKeyItems(
            activeSystemMenu,
            "None (active-system PTT disabled)",
            HandleActiveSystemPttKeyClick);
    }

    private void HandleOpenRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenRecording(metadata);
        }
    }

    private async void HandleDeleteRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel &&
            await ConfirmAsync(
                "Delete recording",
                $"Delete '{metadata.FileName}' and its catalog metadata? This cannot be undone.",
                "Delete"))
        {
            await viewModel.DeleteRecordingAsync(metadata);
        }
    }

    private async void HandlePlayRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlayRecordingAsync(metadata);
        }
    }

    private async void HandleStopRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.StopRecordingPlaybackAsync();
    }

    private void HandleSaveIgnoredSubscribersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.TrySaveRecordingIgnoredSubscribers(channel);
        }
    }

    private void HandleSaveOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveChannelOutputDevice(channel);
        }
    }

    private void HandleSaveWebStreamOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WebStreamViewModel stream } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveWebStreamOutputDevice(stream);
        }
    }

    private void HandleSaveAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.SaveAudioInputPreset();
    }

    private void HandleUseAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseAudioInputPreset(preset);
        }
    }

    private void HandleDeleteAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteAudioInputPreset(preset);
        }
    }

    private void HandleApplyPatchGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ApplyPatchGroup(group);
        }
    }

    private void HandleUseDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseDtmfPreset(preset);
        }
    }

    private void HandleDeleteDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteDtmfPreset(preset);
        }
    }

    private async void HandleSendDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SendDtmfPresetAsync(preset);
        }
    }

    private void HandleUseTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UseTonePreset(preset);
        }
    }

    private void HandleDeleteTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DeleteTonePreset(preset);
        }
    }

    private async void HandleSendTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset } &&
            DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SendTonePresetAsync(preset);
        }
    }
}
