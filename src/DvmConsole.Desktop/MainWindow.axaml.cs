using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Ptt;
using DvmConsole.Presentation;
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
    private readonly ManagedConfigurationLibrary configurationLibrary;
    private readonly DesktopConfigurationMaterializer configurationMaterializer;
    private readonly OperatorViewStore operatorViewStore;
    private readonly LatestOperatorViewWriter operatorViewWriter;
    private readonly OperatorViewSettings operatorViewSettings;
    private readonly EngineeringHealthViewModel engineeringHealthViewModel;
    private readonly ChannelCardsRenderer cardsRenderer;
    private readonly ChannelListView listRenderer;
    private readonly DesktopApplicationLifecycle applicationLifecycle;
    private readonly ChannelPttLifecycleBinding pttLifecycleBinding;
    private readonly bool demoMode;
    private MainWindowViewModel viewModel => sessionHost.ViewModel;
    private ChannelPttController channelPtt => sessionHost.ChannelPtt;
    private OperatorToolsWindow? operatorToolsWindow;
    private ConfigurationStudioWindow? configurationStudioWindow;
    private ConfigurationLibraryWindow? configurationLibraryWindow;
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
    private ConfigurationReference? activeConfiguration;
    private string? pendingStartupConfigurationImportPath;
    private ConsoleRendererPreference effectiveRenderer;
    private int shutdownStarted;
    private bool shutdownComplete;
    internal ConfigurationStudioWindow? OpenConfigurationStudioWindow => configurationStudioWindow;
    internal ManagedConfigurationLibrary ConfigurationLibrary => configurationLibrary;

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
        string appDataRoot = Path.GetDirectoryName(this.sessionUserSettingsStore.Path) ?? AppContext.BaseDirectory;
        configurationLibrary = new ManagedConfigurationLibrary(Path.Combine(appDataRoot, "ConfigurationLibrary"));
        RegisterLegacyConfigurationCandidates();
        configurationMaterializer = new DesktopConfigurationMaterializer(
            configurationLibrary,
            Path.Combine(appDataRoot, "ConfigurationRuntime"));
        bool migrateLegacyConfigurationOperatorState;
        (configurationPath, activeConfiguration, migrateLegacyConfigurationOperatorState) =
            ResolveInitialConfiguration(configurationPath);
        InitializeComponent();
        // Avalonia can leave named controls declared inside nested MenuItems
        // unresolved when the compiled XAML is loaded from a published
        // self-contained apphost. Resolve them from the window name scope
        // before the startup menu refreshes run.
        recentManagedConfigurationsMenu ??= this.FindControl<MenuItem>("recentManagedConfigurationsMenu")
            ?? throw new InvalidOperationException("The managed recent configurations menu was not initialized.");
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
        channelRendererHost ??= this.FindControl<ContentControl>("channelRendererHost")
            ?? throw new InvalidOperationException("The channel renderer host was not initialized.");
        cardsRendererMenuItem ??= this.FindControl<MenuItem>("cardsRendererMenuItem")
            ?? throw new InvalidOperationException("The Cards renderer menu item was not initialized.");
        listRendererMenuItem ??= this.FindControl<MenuItem>("listRendererMenuItem")
            ?? throw new InvalidOperationException("The List renderer menu item was not initialized.");
        activityCallHistoryList ??= this.FindControl<ItemsControl>("activityCallHistoryList")
            ?? throw new InvalidOperationException("The Activity history list was not initialized.");
        MainWindowViewModel initialViewModel = LoadSessionViewModel(
            configurationPath,
            activeConfiguration,
            migrateLegacyConfigurationOperatorState);
        PopulatePttKeyMenus(initialViewModel);
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
        cardsRenderer = CreateCardsRenderer(initialViewModel);
        sessionHost = new MainWindowSessionHost(
            initialViewModel,
            HandleActivityHistoryCollectionChanging,
            replacement => AvaloniaStorageThreading.Invoke(() => ApplySessionDataContext(replacement)),
            () => AvaloniaStorageThreading.Invoke(CloseModelessViewModelWindows),
            () => AvaloniaStorageThreading.Invoke(CloseAllModelessWindows));
        listRenderer = new ChannelListView();
        listRenderer.Attach(sessionHost.ApplicationSession, channelPtt, () => viewModel.TogglePttMode);
        applicationLifecycle = new DesktopApplicationLifecycle(this);
        pttLifecycleBinding = new ChannelPttLifecycleBinding(
            applicationLifecycle,
            cancellationToken => channelPtt.ReleaseAllAsync(cancellationToken),
            exception => DesktopCrashLog.Write("Lifecycle PTT release", exception));
        ApplyChannelRenderer(Width, releasePtt: false);
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
        MainWindowMenuBuilder.ReplaceRecentManagedConfigurationItems(
            recentManagedConfigurationsMenu,
            [],
            "No recently opened configurations",
            HandleOpenRecentManagedConfigurationClick);
        RefreshNamedSettingsProfileMenus();
        Opened += async (_, _) =>
        {
            RefreshResponsiveToolbarVisibility(Bounds.Width);
            mainWindowPlacement.RestorePosition();
            mainWindowPlacement.StartTracking();
            ConfigureTransientChannelScrollBars();
            ConfigureTransientScrollBars(activityScrollViewer);
            await sessionHost.StartAsync();
            if (activeConfiguration is not null)
            {
                try
                {
                    await configurationLibrary.ActivateAsync(activeConfiguration);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    DesktopCrashLog.Write("Managed configuration recent timestamp", exception);
                }
            }
            await RefreshRecentManagedConfigurationMenuAsync();
            if (pendingStartupConfigurationImportPath is { } pendingImport)
            {
                pendingStartupConfigurationImportPath = null;
                await OpenCodeplugAsync(pendingImport);
            }
        };
        LayoutUpdated += (_, _) => ConfigureTransientChannelScrollBars();
        Closing += HandleClosing;
        Activated += (_, _) => UpdatePttFocusSuppression();
        Deactivated += async (_, _) =>
        {
            pttKeyRouter.UpdateInputFocus(null, isWindowActive: false);
            if (Volatile.Read(ref shutdownStarted) != 0)
                return;
            try
            {
                await sessionHost.FlushSettingsIfActiveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref shutdownStarted) != 0)
            {
                // Hiding the window during shutdown raises Deactivated. The
                // session may finish disposing before an in-flight flush resumes.
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                DesktopCrashLog.Write("Operator settings persistence", exception);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    viewModel.ReportUserSettingsPersistenceFailure(exception));
            }
        };
    }

    private MainWindowViewModel LoadSessionViewModel(
        string? configurationPath,
        ConfigurationReference? configurationReference = null,
        bool migrateLegacyConfigurationOperatorState = false)
    {
        MainWindowViewModel loaded = MainWindowViewModel.Load(
            configurationPath,
            sessionUserSettingsStore,
            networkDisabledDemo: demoMode,
            configurationReference: configurationReference,
            useLegacyPathFallback: false,
            migrateLegacyConfigurationOperatorState: migrateLegacyConfigurationOperatorState);
        if (demoMode)
            loaded.InitializeDemoScenario();
        return loaded;
    }

    private (string? Path, ConfigurationReference? Reference, bool MigrateLegacyOperatorState) ResolveInitialConfiguration(
        string? requestedPath)
    {
        UserSettings startupSettings = sessionUserSettingsStore.Load();
        bool persistedStartup = LegacyOperatorStateAttributionPolicy
            .ShouldAttributeToOpenedConfiguration(
                requestedPath,
                startupSettings.LastCodeplugPath);
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            ConfigurationReference? active = configurationLibrary.Active;
            if (active is not null)
            {
                string materialized = configurationMaterializer
                    .MaterializeAsync(active)
                    .AsTask().GetAwaiter().GetResult();
                return (materialized, active, true);
            }
            requestedPath = startupSettings.LastCodeplugPath;
        }

        if (string.IsNullOrWhiteSpace(requestedPath))
            return (null, null, false);

        string legacyPath = Path.GetFullPath(requestedPath);
        try
        {
            ConfigurationImportResult imported = configurationLibrary.ImportAsync(
                    new DesktopConfigurationDocumentSet(legacyPath),
                    new ConfigurationImportOptions())
                .AsTask().GetAwaiter().GetResult();
            configurationLibrary.ActivateAsync(imported.Reference)
                .AsTask().GetAwaiter().GetResult();
            string materialized = configurationMaterializer.MaterializeAsync(imported.Reference)
                .AsTask().GetAwaiter().GetResult();
            MigrateLegacyOperatorState(legacyPath, materialized);
            return (materialized, imported.Reference, persistedStartup);
        }
        catch (ConfigurationExternalCompanionsConfirmationRequiredException exception)
        {
            // An owner window is required before desktop can ask the operator
            // to approve/select out-of-tree companions. Start with the legacy
            // document, then complete the managed import from the Opened flow.
            pendingStartupConfigurationImportPath = legacyPath;
            DesktopCrashLog.Write("Configuration library import awaiting external companions", exception);
            return (legacyPath, null, false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or ConfigurationImportConflictException)
        {
            // Invalid legacy YAML still opens in the existing diagnostics path.
            // Configuration Studio will commit any subsequent edit into the
            // managed library rather than writing this source file.
            DesktopCrashLog.Write("Configuration library import", exception);
            return (legacyPath, null, false);
        }
    }

    private void MigrateLegacyOperatorState(string legacyPath, string managedPath)
    {
        if (FileSystemPathIdentity.AreEquivalent(legacyPath, managedPath))
        {
            return;
        }

        UserSettings settings = sessionUserSettingsStore.Load();
        _ = CodeplugGroupStateStore.CopyForSaveAs(settings, legacyPath, managedPath);
        _ = CodeplugStudioStateStore.CopyForSaveAs(settings, legacyPath, managedPath);
        sessionUserSettingsStore.Save(settings);
    }

    private void RegisterLegacyConfigurationCandidates()
    {
        UserSettings settings = sessionUserSettingsStore.Load();
        LegacyConfigurationCandidate[] candidates = (settings.RecentCodeplugPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Distinct(FileSystemPathIdentity.Comparer)
            .Select(path => new LegacyConfigurationCandidate(Path.GetFileName(path), path))
            .ToArray();
        configurationLibrary.RegisterLegacyCandidatesAsync(candidates)
            .AsTask()
            .GetAwaiter()
            .GetResult();
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
        cardsRenderer.DataContext = replacement;
        engineeringHealthViewModel.ReplaceConsole(replacement);
    }

    private ChannelCardsRenderer CreateCardsRenderer(MainWindowViewModel dataContext)
    {
        var renderer = new ChannelCardsRenderer { DataContext = dataContext };
        renderer.ChannelPointerPressed += HandleChannelPointerPressed;
        renderer.ChannelPointerMoved += HandleChannelPointerMoved;
        renderer.ChannelPointerReleased += HandleChannelPointerReleased;
        renderer.ChannelPointerCaptureLost += HandleChannelPointerCaptureLost;
        renderer.TransmitSelectionClick += HandleTransmitSelectionClick;
        renderer.PageSelectionClick += HandlePageSelectionClick;
        renderer.AlertSelectionClick += HandleAlertSelectionClick;
        return renderer;
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

        WindowPlacementSetting closingPlacement = mainWindowPlacement.GetPlacementForPersistence();
        mainWindowPlacement.Dispose();

        // Remove the console from view immediately while the bounded cleanup
        // finishes releasing PTT, recordings, audio, and network ownership.
        // The native window is closed only after that safety work completes.
        Hide();

        try
        {
            await viewModel.SaveMainWindowPlacementAsync(closingPlacement);
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Main window placement persistence", exception);
        }

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

    private Task ShutdownAsync()
        => BoundedShutdown.RunAsync(ShutdownCoreAsync, ShutdownTimeout);

    private async Task ShutdownCoreAsync()
    {
        var cleanup = new AsyncCleanup();
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
        await cleanup.RunTaskAsync(() => pttLifecycleBinding.DisposeAsync().AsTask());
        cleanup.Run(applicationLifecycle.Dispose);
        await cleanup.RunTaskAsync(() => listRenderer.DetachAsync().AsTask());
        await cleanup.RunTaskAsync(() => sessionHost.DisposeAsync().AsTask());
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
            ChannelId channelId = new(channel.SessionId);
            if (channel.IsTransmitting)
                await channelPtt.UnkeyAsync(channelId);
            else
                await channelPtt.ToggleAsync(channelId);
        }
        else
        {
            e.Pointer.Capture(button);
            await channelPtt.PressAsync(new ChannelId(channel.SessionId));
        }
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Button? button = e.Pointer.Captured as Button ?? FindPttButton(e.Source);
        if (button?.DataContext is not ChannelViewModel channel || !button.Classes.Contains("ptt"))
            return;

        e.Handled = true;
        e.Pointer.Capture(null);
        await channelPtt.ReleaseAsync(new ChannelId(channel.SessionId));
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Button? button = FindPttButton(e.Source);
        if (button?.DataContext is ChannelViewModel channel)
            await channelPtt.ReleaseAsync(new ChannelId(channel.SessionId));
    }

    internal async Task HandleAccessibleChannelPttKeyDownAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (viewModel.TogglePttMode)
        {
            ChannelId channelId = new(channel.SessionId);
            if (channel.IsTransmitting)
                await channelPtt.UnkeyAsync(channelId);
            else
                await channelPtt.ToggleAsync(channelId);
        }
        else
            await channelPtt.PressAsync(new ChannelId(channel.SessionId));
    }

    internal async Task HandleAccessibleChannelPttKeyUpAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!viewModel.TogglePttMode)
            await channelPtt.ReleaseAsync(new ChannelId(channel.SessionId));
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

    private async void HandleImportCodeplugClick(object? sender, RoutedEventArgs e)
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
                Title = "Import Codeplug",
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
            DesktopCrashLog.Write("Import codeplug picker", exception);
            await AvaloniaStorageThreading.InvokeAsync(() => ShowCodeplugErrorAsync(
                $"The codeplug picker could not be opened.\n\n{exception.Message}"));
            return;
        }

        if (files.Count == 0)
            return;

        try
        {
            IStorageFile selected = files[0];
            string? legacyPath = await AvaloniaStorageThreading.Invoke(selected.TryGetLocalPath);
            using AvaloniaStorageConfigurationImportDocumentSet source = await AvaloniaStorageThreading.Invoke(
                () => new AvaloniaStorageConfigurationImportDocumentSet(selected));
            await AvaloniaStorageThreading.InvokeAsync(() => OpenCodeplugAsync(source, legacyPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DesktopCrashLog.Write("Import codeplug picker document", exception);
            await AvaloniaStorageThreading.InvokeAsync(() => ShowCodeplugErrorAsync(
                $"The selected codeplug could not be imported.\n\n{exception.Message}"));
        }
    }

    private async void HandleOpenRecentManagedConfigurationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ConfigurationReference configuration })
            return;

        await ActivateManagedConfigurationAsync(configuration);
    }

    private async Task OpenCodeplugAsync(string path)
    {
        var source = new DesktopConfigurationDocumentSet(path);
        await OpenCodeplugAsync(source, path);
    }

    private async Task OpenCodeplugAsync(
        IImportDocumentSet source,
        string? legacyPath)
    {
        if (configurationStudioWindow is { } studio &&
            !await AvaloniaStorageThreading.InvokeAsync(studio.ConfirmSessionReplacementAsync))
            return;
        configurationStudioWindow = null;

        MainWindowViewModel replacement;
        ConfigurationImportResult imported;
        try
        {
            await sessionHost.PrepareForReplacementAsync();
            imported = await ImportLegacyConfigurationAsync(source);
            string managedPath = await configurationMaterializer.MaterializeAsync(imported.Reference);
            if (!string.IsNullOrWhiteSpace(legacyPath))
                MigrateLegacyOperatorState(legacyPath, managedPath);
            replacement = LoadSessionViewModel(managedPath, imported.Reference);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            DesktopCrashLog.Write("Open codeplug import", exception);
            await ShowCodeplugErrorAsync(
                $"The configuration could not be imported into the managed library.\n\n{exception.Message}");
            return;
        }

        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            await ShowCodeplugErrorAsync(error);
            return;
        }
        try
        {
            await PublishManagedReplacementAsync(imported.Reference, replacement);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DesktopCrashLog.Write("Open codeplug activation", exception);
            await ShowCodeplugErrorAsync(
                $"The imported configuration could not be activated.\n\n{exception.Message}");
        }
    }

    private async ValueTask<ConfigurationImportResult> ImportLegacyConfigurationAsync(
        IImportDocumentSet source)
    {
        var options = new ConfigurationImportOptions();
        while (true)
        {
            try
            {
                return await configurationLibrary.ImportAsync(source, options);
            }
            catch (ConfigurationExternalCompanionsConfirmationRequiredException confirmation)
            {
                if (options.ConfirmExternalCompanions)
                    throw;
                string references = string.Join(
                    Environment.NewLine,
                    confirmation.References.Select(reference => $"• {reference}"));
                bool approved = await ConfirmAsync(
                    "Import external companion files?",
                    "This configuration refers to key or alias files outside its folder. " +
                    "DVM Console will copy the selected files into the managed revision; the originals will remain unchanged.\n\n" +
                    references,
                    source is AvaloniaStorageConfigurationImportDocumentSet
                        ? "Select files"
                        : "Import companions");
                if (!approved)
                    throw new OperationCanceledException();
                if (source is AvaloniaStorageConfigurationImportDocumentSet pickerSource &&
                    !await SelectExternalCompanionsAsync(pickerSource, confirmation.References))
                {
                    throw new OperationCanceledException();
                }
                options = options with { ConfirmExternalCompanions = true };
            }
            catch (ConfigurationImportConflictException conflict)
            {
                if (await ConfirmAsync(
                        "Configuration changed in two places",
                        "Both the imported YAML bundle and its managed configuration changed since the last import. Replace the managed entry with a recoverable new revision?",
                        "Replace existing"))
                {
                    options = options with
                    {
                        ConflictResolution = ConfigurationConflictResolution.ReplaceExisting,
                        ReplaceConfigurationId = conflict.ExistingConfigurationId
                    };
                    continue;
                }
                if (await ConfirmAsync(
                        "Import as a new configuration?",
                        "Keep the existing managed configuration and import this YAML bundle under a new configuration ID?",
                        "Import as new"))
                {
                    options = options with
                    {
                        ConflictResolution = ConfigurationConflictResolution.ImportAsNew,
                        ReplaceConfigurationId = null
                    };
                    continue;
                }
                throw new OperationCanceledException();
            }
        }
    }

    private async Task<bool> SelectExternalCompanionsAsync(
        AvaloniaStorageConfigurationImportDocumentSet source,
        IReadOnlyList<string> references)
    {
        if (!await AvaloniaStorageThreading.Invoke(() => StorageProvider.CanOpen))
            return false;
        foreach (string reference in references)
        {
            IReadOnlyList<IStorageFile> selected = await AvaloniaStorageThreading.InvokeAsync(
                () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Select companion for {reference}",
                    AllowMultiple = false
                }));
            if (selected.Count == 0)
                return false;
            source.AddExplicitCompanion(reference, selected[0]);
        }
        return true;
    }

    private async void HandleNewConfigurationClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: true);

    private void HandleConfigurationLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (configurationLibraryWindow is null)
        {
            configurationLibraryWindow = new ConfigurationLibraryWindow(configurationLibrary);
            configurationLibraryWindow.ActivateRequested += ActivateManagedConfigurationFromLibraryAsync;
            configurationLibraryWindow.Closed += (_, _) => configurationLibraryWindow = null;
            AttachPttInputSafety(configurationLibraryWindow);
            configurationLibraryWindow.Show(this);
            return;
        }
        configurationLibraryWindow.Activate();
    }

    private async void HandleConfigurationStudioClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Overview, createNew: false);

    private async void HandleConfigurationGroupsClick(object? sender, RoutedEventArgs e)
        => await OpenConfigurationStudioAsync(ConfigurationStudioSection.Groups, createNew: false);

    internal async Task OpenConfigurationStudioAsync(
        ConfigurationStudioSection section,
        bool createNew)
    {
        if (configurationStudioWindow is { } existingStudio)
        {
            if (!createNew)
            {
                existingStudio.SelectSection(section);
                return;
            }
            if (!await existingStudio.ConfirmSessionReplacementAsync())
                return;
            configurationStudioWindow = null;
        }

        try
        {
            await viewModel.FlushUserSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            await ShowInformationAsync(
                "Operator settings unavailable",
                $"Configuration Studio could not be opened because current operator settings could not be saved.\n\n{exception.Message}");
            return;
        }
        ConfigurationDocument document;
        // The session owns the document being edited, so its managed identity is
        // authoritative. The window-level active reference can briefly lag a
        // session replacement; using it here could commit this document as a
        // revision of an unrelated library entry.
        ConfigurationId? studioConfigurationId = createNew
            ? null
            : viewModel.ConfigurationReference?.Id;
        try
        {
            string? path = viewModel.CurrentCodeplugPath;
            document = createNew || string.IsNullOrWhiteSpace(path)
                ? ConfigurationDocument.CreateNew()
                : ConfigurationDocument.Open(path);
            if (studioConfigurationId is null)
            {
                ConfigurationDraft draft = await CreateNewManagedStudioDraftAsync();
                studioConfigurationId = draft.Id;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            await ShowInformationAsync("Unable to open Configuration Studio", exception.Message);
            return;
        }

        configurationStudioWindow = new ConfigurationStudioWindow(
            document,
            viewModel,
            sessionUserSettingsStore,
            configurationLibrary,
            configurationMaterializer,
            studioConfigurationId,
            section);
        AttachPttInputSafety(configurationStudioWindow);
        configurationStudioWindow.ReloadRequested += ReloadManagedConfigurationAsync;
        configurationStudioWindow.Closed += (_, _) => configurationStudioWindow = null;
        configurationStudioWindow.FitInitialBoundsToDisplay(
            Screens.ScreenFromWindow(this) ?? Screens.Primary);
        configurationStudioWindow.Show();
    }

    private async ValueTask<ConfigurationDraft> CreateNewManagedStudioDraftAsync()
    {
        try
        {
            return await configurationLibrary.CreateDraftAsync("Untitled Configuration");
        }
        catch (ConfigurationDraftConflictException conflict)
        {
            bool discard = await ConfirmAsync(
                "Unfinished configuration draft",
                "Configuration Studio found an unfinished managed draft from an earlier session. Discard it and start a new configuration?",
                "Discard and start new");
            if (!discard)
                throw new OperationCanceledException();
            await configurationLibrary.DiscardDraftAsync(conflict.ExistingDraft.Id);
            return await configurationLibrary.CreateDraftAsync("Untitled Configuration");
        }
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
            configurationLibrary,
            configurationMaterializer,
            viewModel.ConfigurationReference?.Id,
            section)
        {
            Width = 1488,
            Height = 1058
        };
    }

    private async Task ReloadManagedConfigurationAsync(ConfigurationReference configuration)
    {
        try
        {
            await ReplaceWithManagedConfigurationAsync(configuration);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            await ShowCodeplugErrorAsync(exception.Message);
        }
    }

    private async Task<bool> ActivateManagedConfigurationFromLibraryAsync(
        ConfigurationLibraryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsLegacyCandidate)
        {
            if (string.IsNullOrWhiteSpace(item.LegacyOriginIdentity))
                return false;
            await OpenCodeplugAsync(item.LegacyOriginIdentity);
            return true;
        }
        return await ActivateManagedConfigurationAsync(item.Reference);
    }

    private async Task<bool> ActivateManagedConfigurationAsync(ConfigurationReference configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configurationStudioWindow is { } studio &&
            !await studio.ConfirmSessionReplacementAsync())
        {
            return false;
        }
        configurationStudioWindow = null;

        try
        {
            await ReplaceWithManagedConfigurationAsync(configuration);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            await ShowInformationAsync("Unable to open managed configuration", exception.Message);
            return false;
        }
    }

    private async Task ReplaceWithManagedConfigurationAsync(ConfigurationReference configuration)
    {
        await sessionHost.PrepareForReplacementAsync();
        string path = await configurationMaterializer.MaterializeAsync(configuration);
        MainWindowViewModel replacement = LoadSessionViewModel(path, configuration);
        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            throw new InvalidDataException(error);
        }

        await PublishManagedReplacementAsync(configuration, replacement);
    }

    private async Task PublishManagedReplacementAsync(
        ConfigurationReference configuration,
        MainWindowViewModel replacement)
    {
        var transition = new ActiveConfigurationTransition(configurationLibrary);
        try
        {
            await transition.PublishAsync(
                configuration,
                _ => new ValueTask(ReplaceViewModelAsync(replacement)),
                () => ReferenceEquals(sessionHost.ViewModel, replacement));
            activeConfiguration = configuration;
        }
        catch
        {
            if (ReferenceEquals(sessionHost.ViewModel, replacement))
                activeConfiguration = configuration;
            else
                await replacement.DisposeAsync();
            throw;
        }
    }

    private async Task RefreshRecentManagedConfigurationMenuAsync()
    {
        var configurations = new List<ConfigurationSummary>();
        string emptyHeader = "No recently opened configurations";
        try
        {
            await foreach (ConfigurationSummary summary in configurationLibrary.ListAsync())
                configurations.Add(summary);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DesktopCrashLog.Write("Recent managed configurations", exception);
            emptyHeader = "Recent configurations unavailable";
        }

        AvaloniaStorageThreading.Invoke(() =>
            MainWindowMenuBuilder.ReplaceRecentManagedConfigurationItems(
                recentManagedConfigurationsMenu,
                configurations,
                emptyHeader,
                HandleOpenRecentManagedConfigurationClick));
    }

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

        using IStorageFile file = files[0];
        await using Stream source = await file.OpenReadAsync();
        await viewModel.SetUserBackgroundAsync(
            file.Name,
            MainWindowViewModel.GetImageMediaType(file.Name),
            source);
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

        try
        {
            string? activeCodeplugPath = viewModel.CurrentCodeplugPath;
            await sessionHost.PrepareForReplacementAsync();
            using IStorageFile file = files[0];
            await using Stream source = await file.OpenReadAsync();
            viewModel.ImportSettings(source);
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath, activeConfiguration));
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
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath, activeConfiguration));
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
        if (file is null)
            return;

        try
        {
            string displayName = file.Name;
            using (file)
            {
                await using Stream destination = await file.OpenWriteAsync();
                viewModel.ExportSettings(destination);
            }
            await ShowInformationAsync("Settings exported", $"Settings were exported to {displayName}.");
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
            await ReplaceViewModelAsync(LoadSessionViewModel(activeCodeplugPath, activeConfiguration));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to reset settings", exception.Message);
        }
    }

    internal async Task ReplaceViewModelAsync(MainWindowViewModel replacement)
    {
        await channelPtt.ReleaseAllAsync();
        await AvaloniaStorageThreading.InvokeAsync(() => listRenderer.DetachAsync().AsTask());
        await sessionHost.ReplaceAsync(replacement);
        AvaloniaStorageThreading.Invoke(() =>
        {
            listRenderer.Attach(sessionHost.ApplicationSession, channelPtt, () => viewModel.TogglePttMode);
            ApplyChannelRenderer(Bounds.Width, releasePtt: false);
            RefreshNamedSettingsProfileMenus();
        });
        await RefreshRecentManagedConfigurationMenuAsync();
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

        ConfigurationLibraryWindow? library = configurationLibraryWindow;
        configurationLibraryWindow = null;
        library?.Close();
    }

    private Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Reset")
        => AvaloniaStorageThreading.InvokeAsync(
            () => ConfirmOnUiThreadAsync(title, message, confirmLabel));

    private async Task<bool> ConfirmOnUiThreadAsync(string title, string message, string confirmLabel)
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

    private Task ShowCodeplugErrorAsync(string message)
        => AvaloniaStorageThreading.InvokeAsync(() => ShowCodeplugErrorOnUiThreadAsync(message));

    private async Task ShowCodeplugErrorOnUiThreadAsync(string message)
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

    private Task ShowInformationAsync(string title, string message)
        => AvaloniaStorageThreading.InvokeAsync(() => ShowInformationOnUiThreadAsync(title, message));

    private async Task ShowInformationOnUiThreadAsync(string title, string message)
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
            => pttKeyRouter.UpdateInputFocus(
                window.FocusManager?.GetFocusedElement(),
                window.IsActive);

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
        window.Deactivated += (_, _) => pttKeyRouter.UpdateInputFocus(null, isWindowActive: false);
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
        RefreshPttKeyMenuSelections();
    }

    private async void HandleActiveSystemPttKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
            return;
        await viewModel.SetActiveSystemPttKeyAsync(key);
        RefreshPttKeyMenuSelections();
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
        => pttKeyRouter.UpdateInputFocus(FocusManager?.GetFocusedElement(), IsActive);

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

    private void PopulatePttKeyMenus(MainWindowViewModel initialViewModel)
    {
        MenuItem globalMenu = this.FindControl<MenuItem>("globalPttKeyMenu")
            ?? throw new InvalidOperationException("The global PTT key menu was not initialized.");
        MenuItem activeSystemMenu = this.FindControl<MenuItem>("activeSystemPttKeyMenu")
            ?? throw new InvalidOperationException("The active-system PTT key menu was not initialized.");
        MainWindowMenuBuilder.ReplacePttKeyItems(
            globalMenu,
            "None (keyboard PTT disabled)",
            initialViewModel.AppliedGlobalPttKey,
            HandleGlobalPttKeyClick);
        MainWindowMenuBuilder.ReplacePttKeyItems(
            activeSystemMenu,
            "None (active-system PTT disabled)",
            initialViewModel.AppliedActiveSystemPttKey,
            HandleActiveSystemPttKeyClick);
        globalMenu.SubmenuOpened += (_, _) => RefreshPttKeyMenuSelections();
        activeSystemMenu.SubmenuOpened += (_, _) => RefreshPttKeyMenuSelections();
    }

    private void RefreshPttKeyMenuSelections()
    {
        MenuItem globalMenu = this.FindControl<MenuItem>("globalPttKeyMenu")
            ?? throw new InvalidOperationException("The global PTT key menu was not initialized.");
        MenuItem activeSystemMenu = this.FindControl<MenuItem>("activeSystemPttKeyMenu")
            ?? throw new InvalidOperationException("The active-system PTT key menu was not initialized.");
        MainWindowMenuBuilder.UpdatePttKeySelection(globalMenu, viewModel.AppliedGlobalPttKey);
        MainWindowMenuBuilder.UpdatePttKeySelection(
            activeSystemMenu,
            viewModel.AppliedActiveSystemPttKey);
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
