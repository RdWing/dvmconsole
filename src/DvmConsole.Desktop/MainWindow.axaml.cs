// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
using System.Reflection;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window, IOperatorCommandSurface
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly MainWindowSessionHost sessionHost;
    private readonly SettingsTransferCoordinator settingsTransfer;
    private readonly WindowPttKeyRouter pttKeyRouter;
    private readonly OperatorCommandController operatorCommands;
    private readonly UserSettingsStore sessionUserSettingsStore;
    private readonly ManagedConfigurationLibrary configurationLibrary;
    private readonly ManagedConfigurationCommandController configurationCommands;
    private readonly DesktopConfigurationMaterializer configurationMaterializer;
    private readonly OperatorViewStore operatorViewStore;
    private readonly LatestOperatorViewWriter operatorViewWriter;
    private readonly OperatorViewSettings operatorViewSettings;
    private readonly EngineeringHealthViewModel engineeringHealthViewModel;
    private readonly ShutdownTimingRecorder shutdownTiming;
    private readonly ShutdownBackgroundWorkRegistry abandonedShutdownWork = new(
        (phase, exception) => DesktopCrashLog.Write(
            $"Abandoned shutdown work ({phase})",
            exception));
    private readonly ChannelCardsRenderer cardsRenderer;
    private readonly ChannelCardInteractionController channelCardInteractions;
    private readonly ChannelListView listRenderer;
    private readonly ChannelRendererController channelRenderer;
    private readonly ResponsiveStatusController responsiveStatus;
    private readonly DesktopApplicationLifecycle applicationLifecycle;
    private readonly ChannelPttLifecycleBinding pttLifecycleBinding;
    private readonly bool demoMode;
    private MainWindowViewModel viewModel => sessionHost.ViewModel;
    private ChannelPttController channelPtt => sessionHost.ChannelPtt;
    private readonly ModelessWindowController modelessWindows = new();
    private readonly List<DispatcherTimer> scrollBarTimers = [];
    private readonly Dictionary<ScrollViewer, ScrollBar[]> configuredScrollViewers = [];
    private readonly ActivityHistoryViewportController activityHistoryViewport;
    private readonly MainWindowPlacementController mainWindowPlacement;
    private readonly PttHoldTracker<IPointer> heldPttPointers = new();
    private ConfigurationReference? activeConfiguration;
    private IConfigurationMaterializationLease? activeMaterializationLease;
    private ConfigurationReference? pendingStartupActiveConfiguration;
    private string? pendingStartupConfigurationImportPath;
    private int shutdownStarted;
    private bool shutdownComplete;
    private const string OperatorToolsWindowKey = "operator-tools";
    private const string ConfigurationStudioWindowKey = "configuration-studio";
    private const string ConfigurationLibraryWindowKey = "configuration-library";
    private const string DebugLogWindowKey = "debug-logs";
    private const string DocumentationWindowKey = "documentation";
    private const string AboutWindowKey = "about";
    private OperatorToolsWindow? operatorToolsWindow
    {
        get => modelessWindows.Get<OperatorToolsWindow>(OperatorToolsWindowKey);
        set => TrackModeless(OperatorToolsWindowKey, value, sessionBound: true, static window => window.Close());
    }
    private ConfigurationStudioWindow? configurationStudioWindow
    {
        get => modelessWindows.Get<ConfigurationStudioWindow>(ConfigurationStudioWindowKey);
        set => TrackModeless(
            ConfigurationStudioWindowKey,
            value,
            sessionBound: true,
            static window => window.CloseForSessionReplacement());
    }
    private ConfigurationLibraryWindow? configurationLibraryWindow
    {
        get => modelessWindows.Get<ConfigurationLibraryWindow>(ConfigurationLibraryWindowKey);
        set => TrackModeless(ConfigurationLibraryWindowKey, value, sessionBound: false, static window => window.Close());
    }
    private DebugLogWindow? debugLogWindow
    {
        get => modelessWindows.Get<DebugLogWindow>(DebugLogWindowKey);
        set => TrackModeless(DebugLogWindowKey, value, sessionBound: true, static window => window.Close());
    }
    private DocumentationWindow? documentationWindow
    {
        get => modelessWindows.Get<DocumentationWindow>(DocumentationWindowKey);
        set => TrackModeless(DocumentationWindowKey, value, sessionBound: false, static window => window.Close());
    }
    private AboutWindow? aboutWindow
    {
        get => modelessWindows.Get<AboutWindow>(AboutWindowKey);
        set => TrackModeless(AboutWindowKey, value, sessionBound: false, static window => window.Close());
    }
    internal ConfigurationStudioWindow? OpenConfigurationStudioWindow => configurationStudioWindow;
    internal ManagedConfigurationLibrary ConfigurationLibrary => configurationLibrary;

    private void TrackModeless<TWindow>(
        string key,
        TWindow? window,
        bool sessionBound,
        Action<TWindow> close)
        where TWindow : class
    {
        if (window is null)
        {
            modelessWindows.Forget<TWindow>(key);
            return;
        }
        modelessWindows.Track(key, window, sessionBound, close);
    }

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
        : this(configurationPath, sessionUserSettingsStore, operatorViewStore, demoMode, null)
    {
    }

    private MainWindow(
        string? configurationPath,
        UserSettingsStore sessionUserSettingsStore,
        OperatorViewStore operatorViewStore,
        bool demoMode,
        DesktopConfigurationStoreContext? configurationStores)
    {
        this.sessionUserSettingsStore = sessionUserSettingsStore ??
            throw new ArgumentNullException(nameof(sessionUserSettingsStore));
        this.operatorViewStore = operatorViewStore ??
            throw new ArgumentNullException(nameof(operatorViewStore));
        this.demoMode = demoMode;
        string appDataRoot = Path.GetDirectoryName(this.sessionUserSettingsStore.Path) ?? AppContext.BaseDirectory;
        shutdownTiming = new ShutdownTimingRecorder(appDataRoot);
        DesktopConfigurationStoreContext stores = configurationStores ??
            DesktopConfigurationStoreContext.Create(appDataRoot);
        configurationLibrary = stores.Library;
        configurationCommands = stores.Commands;
        configurationMaterializer = stores.Materializer;
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
        fullStatusBar ??= this.FindControl<Grid>("fullStatusBar")
            ?? throw new InvalidOperationException("The full status bar was not initialized.");
        compactStatusBar ??= this.FindControl<Button>("compactStatusBar")
            ?? throw new InvalidOperationException("The compact status bar was not initialized.");
        responsiveStatus = new ResponsiveStatusController(
            fullStatusBar,
            compactStatusBar);
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
        var activityViewportAnchor = new ScrollViewportAnchor<CallHistoryEntry>(
            () => activityScrollViewer,
            () => activityCallHistoryList.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("activity-call-card")),
            control => control.DataContext as CallHistoryEntry);
        activityHistoryViewport = new ActivityHistoryViewportController(
            activityCallHistoryList,
            activityViewportAnchor);
        channelCardInteractions = new ChannelCardInteractionController(this, () => viewModel);
        cardsRenderer = CreateCardsRenderer(initialViewModel);
        sessionHost = new MainWindowSessionHost(
            initialViewModel,
            activityHistoryViewport.HandleCollectionChanging,
            replacement => AvaloniaStorageThreading.Invoke(() => ApplySessionDataContext(replacement)),
            () => AvaloniaStorageThreading.Invoke(CloseModelessViewModelWindows),
            () => AvaloniaStorageThreading.Invoke(CloseAllModelessWindows));
        settingsTransfer = new SettingsTransferCoordinator(CaptureSettingsTransferSession);
        sessionHost.ReplacementFollowUpFailed += HandleSessionReplacementFollowUpFailed;
        listRenderer = new ChannelListView();
        listRenderer.Attach(sessionHost.ApplicationSession, channelPtt, () => viewModel.TogglePttMode);
        channelRenderer = new ChannelRendererController(
            operatorViewSettings,
            channelRendererHost,
            cardsRenderer,
            listRenderer,
            cardsRendererMenuItem,
            listRendererMenuItem);
        applicationLifecycle = new DesktopApplicationLifecycle(this);
        pttLifecycleBinding = new ChannelPttLifecycleBinding(
            applicationLifecycle,
            ReleaseAllChannelPttAsync,
            exception => DesktopCrashLog.Write("Lifecycle PTT release", exception));
        channelRenderer.Apply(Width);
        pttKeyRouter = new WindowPttKeyRouter(() => viewModel);
        operatorCommands = new OperatorCommandController(this);
        ApplyEngineeringHealthVisibility();
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
            ConfigureTransientScrollBars(activityScrollViewer);
            await sessionHost.StartAsync();
            await RegisterLegacyConfigurationCandidatesAsync();
            if (pendingStartupActiveConfiguration is { } pendingActive)
            {
                pendingStartupActiveConfiguration = null;
                await ActivateManagedConfigurationAsync(pendingActive);
            }
            else if (pendingStartupConfigurationImportPath is { } pendingImport)
            {
                pendingStartupConfigurationImportPath = null;
                await OpenCodeplugAsync(pendingImport);
            }
            await RefreshRecentManagedConfigurationMenuAsync();
        };
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

    internal static async Task<MainWindow> CreateAsync(
        string? configurationPath,
        UserSettingsStore sessionUserSettingsStore,
        OperatorViewStore operatorViewStore,
        bool demoMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionUserSettingsStore);
        ArgumentNullException.ThrowIfNull(operatorViewStore);
        string appDataRoot = Path.GetDirectoryName(sessionUserSettingsStore.Path) ?? AppContext.BaseDirectory;
        DesktopConfigurationStoreContext stores = await DesktopConfigurationStoreContext.CreateAsync(
            appDataRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => new MainWindow(
                configurationPath,
                sessionUserSettingsStore,
                operatorViewStore,
                demoMode,
                stores));
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
            migrateLegacyConfigurationOperatorState: migrateLegacyConfigurationOperatorState,
            serviceDisposalObserved: shutdownTiming.ObserveService);
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
                pendingStartupActiveConfiguration = active;
                return (null, null, false);
            }
            requestedPath = startupSettings.LastCodeplugPath;
        }

        if (string.IsNullOrWhiteSpace(requestedPath))
            return (null, null, false);

        string legacyPath = Path.GetFullPath(requestedPath);
        if (demoMode)
            return ResolveImmediateDemoConfiguration(legacyPath, persistedStartup);

        pendingStartupConfigurationImportPath = legacyPath;
        return (legacyPath, null, false);
    }

    private (string Path, ConfigurationReference? Reference, bool MigrateLegacyOperatorState)
        ResolveImmediateDemoConfiguration(string legacyPath, bool persistedStartup)
    {
        try
        {
            // The deterministic demo is an explicit offline/test mode whose
            // Show() contract requires a complete managed identity. Normal
            // operator startup takes the asynchronous Opened path above.
            ConfigurationImportResult imported = configurationLibrary.ImportAsync(
                    new DesktopConfigurationDocumentSet(legacyPath),
                    new ConfigurationImportOptions())
                .AsTask().GetAwaiter().GetResult();
            configurationLibrary.ActivateAsync(imported.Reference)
                .AsTask().GetAwaiter().GetResult();
            IConfigurationMaterializationLease materialization = configurationMaterializer
                .MaterializeAsync(imported.Reference)
                .AsTask().GetAwaiter().GetResult();
            MigrateLegacyOperatorState(legacyPath, materialization.Path);
            activeMaterializationLease = materialization;
            return (materialization.Path, imported.Reference, persistedStartup);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or
            ConfigurationImportConflictException or ConfigurationExternalCompanionsConfirmationRequiredException)
        {
            DesktopCrashLog.Write("Demo configuration library import", exception);
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

    private async Task RegisterLegacyConfigurationCandidatesAsync()
    {
        try
        {
            UserSettings settings = sessionUserSettingsStore.Load();
            LegacyConfigurationCandidate[] candidates = (settings.RecentCodeplugPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path))
                .Distinct(FileSystemPathIdentity.Comparer)
                .Select(path => new LegacyConfigurationCandidate(Path.GetFileName(path), path))
                .ToArray();
            await configurationLibrary.RegisterLegacyCandidatesAsync(candidates);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            DesktopCrashLog.Write("Legacy configuration discovery", exception);
        }
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
        renderer.ChannelPointerPressed += channelCardInteractions.HandlePointerPressed;
        renderer.ChannelPointerMoved += channelCardInteractions.HandlePointerMoved;
        renderer.ChannelPointerReleased += channelCardInteractions.HandlePointerReleased;
        renderer.ChannelPointerCaptureLost += channelCardInteractions.HandlePointerCaptureLost;
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
        shutdownTiming.Begin();

        // Remove the console from view immediately while the bounded cleanup
        // finishes releasing PTT, recordings, audio, and network ownership.
        // The native window is closed only after that safety work completes.
        Hide();

        try
        {
            await ShutdownAsync(closingPlacement);
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Main window shutdown", exception);
        }
        finally
        {
            shutdownTiming.Complete();
            shutdownComplete = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private async Task ShutdownAsync(WindowPlacementSetting closingPlacement)
    {
        viewModel.SuppressSessionInputForTransition();
        await BoundedShutdown.RunAsync(
            [
                new ShutdownPhase(
                    "ptt-release",
                    TimeSpan.FromMilliseconds(1_500),
                    cancellationToken => shutdownTiming.MeasureAsync(
                        "ptt-release",
                        async () =>
                        {
                            var cleanup = new AsyncCleanup();
                            await cleanup.RunTaskAsync(
                                () => ReleaseAllChannelPttAsync(cancellationToken).AsTask());
                            await cleanup.RunTaskAsync(
                                () => viewModel.ReleaseAllPttForShutdownAsync(cancellationToken));
                            cleanup.ThrowIfFailed();
                        })),
                new ShutdownPhase(
                    "network-quiesce",
                    TimeSpan.FromMilliseconds(1_500),
                    cancellationToken => shutdownTiming.MeasureAsync(
                        "network-quiesce",
                        () => viewModel.QuiesceFneSessionAsync(cancellationToken))),
                new ShutdownPhase(
                    "accepted-recording-drain",
                    TimeSpan.FromMilliseconds(1_500),
                    cancellationToken => shutdownTiming.MeasureAsync(
                        "accepted-recording-drain",
                        () => viewModel.DrainAcceptedRecordingWorkAsync(cancellationToken))),
                new ShutdownPhase(
                    "settings-persistence",
                    TimeSpan.FromSeconds(1),
                    cancellationToken => shutdownTiming.MeasureAsync(
                        "settings-persistence",
                        () => PersistForShutdownAsync(closingPlacement, cancellationToken))),
                new ShutdownPhase(
                    "ui-detachment",
                    TimeSpan.FromSeconds(1),
                    _ => shutdownTiming.MeasureAsync(
                        "ui-detachment",
                        DetachUiForShutdownAsync)),
                new ShutdownPhase(
                    "session-disposal",
                    TimeSpan.FromSeconds(5),
                    _ => shutdownTiming.MeasureAsync(
                        "session-disposal",
                        () => sessionHost.DisposeAsync().AsTask())),
                new ShutdownPhase(
                    "materialization-release",
                    TimeSpan.FromSeconds(1),
                    _ => shutdownTiming.MeasureAsync(
                        "materialization-release",
                        ReleaseMaterializationForShutdownAsync))
            ],
            ShutdownTimeout,
            () =>
            {
                viewModel.ApplyFinalShutdownSafetyFence();
            },
            abandonedShutdownWork.Register);
    }

    private async Task PersistForShutdownAsync(
        WindowPlacementSetting closingPlacement,
        CancellationToken cancellationToken)
    {
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(
            () => sessionHost.FlushSettingsIfActiveAsync(cancellationToken));
        await cleanup.RunTaskAsync(
            () => viewModel.SaveMainWindowPlacementAsync(closingPlacement));
        cleanup.Run(() => AvaloniaStorageThreading.Invoke(CaptureEngineeringHealthHeight));
        await cleanup.RunTaskAsync(() => operatorViewWriter.DisposeAsync().AsTask());
        cleanup.ThrowIfFailed();
    }

    private Task DetachUiForShutdownAsync()
        => AvaloniaStorageThreading.InvokeAsync(async () =>
        {
            var cleanup = new AsyncCleanup();
            foreach (DispatcherTimer timer in scrollBarTimers)
                timer.Stop();
            cleanup.Run(activityHistoryViewport.Dispose);
            cleanup.Run(CloseAllModelessWindows);
            await cleanup.RunTaskAsync(() => engineeringHealthViewModel.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => pttLifecycleBinding.DisposeAsync().AsTask());
            cleanup.Run(applicationLifecycle.Dispose);
            await cleanup.RunTaskAsync(() => listRenderer.DetachAsync().AsTask());
            cleanup.ThrowIfFailed();
        });

    private async Task ReleaseMaterializationForShutdownAsync()
    {
        IConfigurationMaterializationLease? lease = Interlocked.Exchange(
            ref activeMaterializationLease,
            null);
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
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
                await RunPointerPttActionAsync(() => channelPtt.UnkeyAsync(channelId));
            else
                await RunPointerPttActionAsync(() => channelPtt.ToggleAsync(channelId));
        }
        else
        {
            heldPttPointers.Track(e.Pointer, new ChannelId(channel.SessionId));
            e.Pointer.Capture(button);
            await RunPointerPttActionAsync(
                () => channelPtt.PressAsync(new ChannelId(channel.SessionId)));
        }
    }

    private async void HandlePttPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Button? button = e.Pointer.Captured as Button ?? FindPttButton(e.Source);
        ChannelId? heldChannel = heldPttPointers.Take(e.Pointer);
        ChannelId? channelId = heldChannel ??
            (button?.DataContext is ChannelViewModel channel && button.Classes.Contains("ptt")
                ? new ChannelId(channel.SessionId)
                : null);
        if (channelId is null)
            return;

        e.Handled = true;
        e.Pointer.Capture(null);
        await RunPointerPttActionAsync(() => channelPtt.ReleaseAsync(channelId.Value));
    }

    private async void HandlePttPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ChannelId? heldChannel = heldPttPointers.Take(e.Pointer);
        Button? button = FindPttButton(e.Source);
        ChannelId? channelId = heldChannel ??
            (button?.DataContext is ChannelViewModel channel
                ? new ChannelId(channel.SessionId)
                : null);
        if (channelId is not null)
            await RunPointerPttActionAsync(() => channelPtt.ReleaseAsync(channelId.Value));
    }

    private static async Task RunPointerPttActionAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            // The view model keeps unresolved ownership visible and provides
            // the operator-facing status. Never let an async-void pointer
            // callback turn a recoverable PTT failure into a process crash.
            DesktopCrashLog.Write("Channel PTT pointer action", exception);
        }
    }

    private async ValueTask ReleaseAllChannelPttAsync(CancellationToken cancellationToken = default)
    {
        heldPttPointers.Clear();
        await channelPtt.ReleaseAllAsync(cancellationToken);
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
        IConfigurationMaterializationLease? materialization = null;
        ConfigurationImportResult imported;
        try
        {
            await sessionHost.PrepareForReplacementAsync();
            imported = await ImportLegacyConfigurationAsync(source);
            materialization = await configurationMaterializer.MaterializeAsync(imported.Reference);
            string managedPath = materialization.Path;
            if (!string.IsNullOrWhiteSpace(legacyPath))
                MigrateLegacyOperatorState(legacyPath, managedPath);
            replacement = LoadSessionViewModel(managedPath, imported.Reference);
        }
        catch (OperationCanceledException)
        {
            if (materialization is not null)
                await materialization.DisposeAsync();
            return;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (materialization is not null)
                await materialization.DisposeAsync();
            DesktopCrashLog.Write("Open codeplug import", exception);
            await ShowCodeplugErrorAsync(
                $"The configuration could not be imported into the managed library.\n\n{exception.Message}");
            return;
        }

        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            await materialization.DisposeAsync();
            await ShowCodeplugErrorAsync(error);
            return;
        }
        try
        {
            await PublishManagedReplacementAsync(imported.Reference, replacement, materialization);
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
        bool usesDocumentPicker = source is AvaloniaStorageConfigurationImportDocumentSet;
        return await configurationCommands.ImportAsync(
            source,
            usesDocumentPicker,
            ConfirmAsync,
            references => usesDocumentPicker
                ? SelectExternalCompanionsAsync(
                    (AvaloniaStorageConfigurationImportDocumentSet)source,
                    references)
                : Task.FromResult(true));
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
            var libraryWindow = new ConfigurationLibraryWindow(configurationLibrary);
            configurationLibraryWindow = libraryWindow;
            libraryWindow.ActivateRequested += ActivateManagedConfigurationFromLibraryAsync;
            libraryWindow.Closed += (_, _) =>
                modelessWindows.Forget(ConfigurationLibraryWindowKey, libraryWindow);
            AttachPttInputSafety(libraryWindow);
            libraryWindow.Show(this);
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

        try
        {
            var studioWindow = new ConfigurationStudioWindow(
                document,
                viewModel,
                sessionUserSettingsStore,
                configurationLibrary,
                configurationMaterializer,
                studioConfigurationId,
                section);
            configurationStudioWindow = studioWindow;
            AttachPttInputSafety(studioWindow);
            studioWindow.ReloadRequested += ReloadManagedConfigurationAsync;
            studioWindow.Closed += (_, _) =>
                modelessWindows.Forget(ConfigurationStudioWindowKey, studioWindow);
            studioWindow.FitInitialBoundsToDisplay(
                Screens.ScreenFromWindow(this) ?? Screens.Primary);
            studioWindow.Show();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException or NotSupportedException or
            System.Security.SecurityException or YamlDotNet.Core.YamlException)
        {
            configurationStudioWindow = null;
            DesktopCrashLog.Write("Configuration Studio initialization", exception);
            await ShowInformationAsync("Unable to open Configuration Studio", exception.Message);
        }
    }

    private async ValueTask<ConfigurationDraft> CreateNewManagedStudioDraftAsync()
        => await configurationCommands.CreateDraftAsync(ConfirmAsync);

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
        IConfigurationMaterializationLease materialization =
            await configurationMaterializer.MaterializeAsync(configuration);
        MainWindowViewModel replacement = LoadSessionViewModel(materialization.Path, configuration);
        if (!replacement.IsCodeplugLoaded)
        {
            string error = replacement.StatusText;
            await replacement.DisposeAsync();
            await materialization.DisposeAsync();
            throw new InvalidDataException(error);
        }

        await PublishManagedReplacementAsync(configuration, replacement, materialization);
    }

    private async Task PublishManagedReplacementAsync(
        ConfigurationReference configuration,
        MainWindowViewModel replacement,
        IConfigurationMaterializationLease materialization)
    {
        var transition = new ActiveConfigurationTransition(configurationLibrary);
        IConfigurationMaterializationLease? previousMaterialization = activeMaterializationLease;
        try
        {
            await transition.PublishAsync(
                configuration,
                _ => new ValueTask(ReplaceViewModelAsync(replacement)),
                () => ReferenceEquals(sessionHost.ViewModel, replacement));
            activeConfiguration = configuration;
            activeMaterializationLease = materialization;
            if (previousMaterialization is not null)
                await previousMaterialization.DisposeAsync();
        }
        catch
        {
            if (ReferenceEquals(sessionHost.ViewModel, replacement))
            {
                activeConfiguration = configuration;
                activeMaterializationLease = materialization;
                if (previousMaterialization is not null)
                    await previousMaterialization.DisposeAsync();
            }
            else
            {
                await replacement.DisposeAsync();
                await materialization.DisposeAsync();
            }
            throw;
        }
    }

    private static void HandleSessionReplacementFollowUpFailed(
        object? sender,
        SessionReplacementFollowUpFailure failure)
    {
        string operation = failure.Phase switch
        {
            SessionReplacementFollowUpPhase.RetiredSessionCleanup =>
                "Retired configuration session cleanup",
            SessionReplacementFollowUpPhase.SelectedWebStreamRestore =>
                "Selected web-stream restore",
            _ => "Configuration replacement follow-up"
        };
        DesktopCrashLog.Write(operation, failure.Exception);
        AvaloniaStorageThreading.Invoke(() =>
            failure.ActiveViewModel.ReportSessionReplacementFollowUpFailure(failure.Phase));
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

    private async void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(
                "Reset widget layout",
                "Reset all channel widget positions and lock the layout? You can undo this for eight seconds.",
                "Reset"))
        {
            viewModel.ResetLayout();
        }
    }

    private async void HandleUndoLastOperatorActionClick(object? sender, RoutedEventArgs e)
        => await viewModel.UndoLastOperatorActionAsync();

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
            using IStorageFile file = files[0];
            await using Stream source = await file.OpenReadAsync();
            SettingsImportStage stage = viewModel.StageSettingsImport(source, file.Name);
            if (!await ConfirmAsync(
                    "Import settings",
                    stage.Preview.SummaryText + "\n\nApply these settings to the current console?",
                    "Continue"))
            {
                return;
            }

            bool acceptRecordingPolicy = await ConfirmImportedRecordingPolicyAsync(stage.Preview);
            if (stage.Preview.RecordingPolicyWillChange && !acceptRecordingPolicy)
                return;

            await settingsTransfer.ImportAsync(stage, acceptRecordingPolicy: acceptRecordingPolicy);
            await ShowInformationAsync("Settings imported", "The imported profile has been applied to the current console.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
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

        SettingsImportStage stage;
        try
        {
            stage = viewModel.StageNamedSettingsProfile(profileName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to preview settings profile", exception.Message);
            RefreshNamedSettingsProfileMenus();
            return;
        }

        if (!await ConfirmAsync(
                "Load settings profile",
                $"{stage.Preview.SummaryText}\n\nApply operator settings from '{profileName}'? The active codeplug and current channel selection will remain unchanged.",
                "Continue"))
        {
            return;
        }

        bool acceptRecordingPolicy = await ConfirmImportedRecordingPolicyAsync(stage.Preview);
        if (stage.Preview.RecordingPolicyWillChange && !acceptRecordingPolicy)
            return;

        try
        {
            await settingsTransfer.ImportAsync(
                stage, SettingsImportScope.OperatorState, acceptRecordingPolicy, profileName);
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

    private async Task<bool> ConfirmImportedRecordingPolicyAsync(SettingsImportPreview preview)
    {
        if (!preview.RecordingPolicyWillChange)
            return false;

        try
        {
            RecordingPolicyImpact impact = await viewModel.PreviewRecordingPolicyAsync(
                preview.RecordingRootPath,
                preview.RecordingRetentionDays);
            return await ConfirmAsync(
                "Confirm imported recording policy",
                impact.SummaryText +
                "\n\nThe imported recording location or retention differs from the current policy. " +
                "Apply it and prune the listed recordings?",
                impact.CandidateCount == 0 ? "Apply policy" : $"Apply and delete {impact.CandidateCount:N0}");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowInformationAsync("Unable to preview imported recording policy", exception.Message);
            return false;
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

        try
        {
            await settingsTransfer.ResetAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowInformationAsync("Unable to reset settings", exception.Message);
        }
    }

    private ISettingsTransferSession CaptureSettingsTransferSession()
    {
        MainWindowViewModel owner = viewModel;
        string? path = owner.CurrentCodeplugPath;
        ConfigurationReference? configuration = activeConfiguration;
        return new DesktopSettingsTransferSession(
            owner, sessionHost,
            () => LoadSessionViewModel(path, configuration),
            ReplaceViewModelAsync);
    }

    internal async Task ReplaceViewModelAsync(MainWindowViewModel replacement)
    {
        await ReleaseAllChannelPttAsync();
        await AvaloniaStorageThreading.InvokeAsync(() => listRenderer.DetachAsync().AsTask());
        try
        {
            await sessionHost.ReplaceAsync(replacement);
        }
        catch
        {
            AvaloniaStorageThreading.Invoke(() =>
            {
                listRenderer.Attach(
                    sessionHost.ApplicationSession,
                    channelPtt,
                    () => viewModel.TogglePttMode);
                channelRenderer.Apply(Bounds.Width);
            });
            throw;
        }
        AvaloniaStorageThreading.Invoke(() =>
        {
            listRenderer.Attach(sessionHost.ApplicationSession, channelPtt, () => viewModel.TogglePttMode);
            channelRenderer.Apply(Bounds.Width);
            RefreshNamedSettingsProfileMenus();
        });
        await RefreshRecentManagedConfigurationMenuAsync();
    }

    private void CloseModelessViewModelWindows()
        => modelessWindows.CloseSessionBound();

    private void CloseAllModelessWindows()
        => modelessWindows.CloseAll();

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

    MainWindowViewModel IOperatorCommandSurface.Session => viewModel;
    Task IOperatorCommandSurface.OpenSubscriberCommandAsync(P25SubscriberCommand command)
        => OpenSubscriberCommandAsync(command);
    void IOperatorCommandSurface.OpenTool(OperatorToolSection section) => OpenOperatorTools(section);
    void IOperatorCommandSurface.ShowDebugLogs() => ShowDebugLogs();
    void IOperatorCommandSurface.ToggleEngineeringHealth()
        => SetEngineeringHealthVisible(!operatorViewSettings.EngineeringHealthVisible);
    void IOperatorCommandSurface.ShowDocumentation() => ShowDocumentation();
    void IOperatorCommandSurface.ShowAbout() => ShowAbout();

    private void ShowDebugLogs()
    {
        if (debugLogWindow is null)
        {
            var logsWindow = new DebugLogWindow(viewModel);
            debugLogWindow = logsWindow;
            AttachPttInputSafety(logsWindow);
            logsWindow.Closed += (_, _) =>
                modelessWindows.Forget(DebugLogWindowKey, logsWindow);
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
            var toolsWindow = new OperatorToolsWindow(viewModel, section, pttKeyRouter);
            operatorToolsWindow = toolsWindow;
            toolsWindow.Closed += (_, _) =>
                modelessWindows.Forget(OperatorToolsWindowKey, toolsWindow);
            toolsWindow.Show();
            return;
        }

        operatorToolsWindow.SelectSection(section);
        operatorToolsWindow.Activate();
    }

    private void ShowDocumentation()
    {
        if (documentationWindow is null)
        {
            var docsWindow = new DocumentationWindow();
            documentationWindow = docsWindow;
            AttachPttInputSafety(docsWindow);
            docsWindow.Closed += (_, _) =>
                modelessWindows.Forget(DocumentationWindowKey, docsWindow);
        }

        if (!documentationWindow.IsVisible)
            documentationWindow.Show(this);
        documentationWindow.Activate();
    }

    private void ShowAbout()
    {
        if (aboutWindow is null)
        {
            var informationWindow = new AboutWindow();
            aboutWindow = informationWindow;
            AttachPttInputSafety(informationWindow);
            informationWindow.Closed += (_, _) =>
                modelessWindows.Forget(AboutWindowKey, informationWindow);
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

    private void HandleToolbarToneContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { Tag: BuiltInAlertToneViewModel button } control)
            return;
        var reset = new MenuItem { Header = $"Restore ALERT {(int)button.Tone}" };
        reset.Click += (_, _) => viewModel.AssignToolbarTone(button, null);
        var alerts = CreateAssignmentMenu("Saved Alerts", viewModel.TonePresets.Select(preset =>
            CreateChoice(preset.Name, false, () => viewModel.AssignToolbarTone(button, preset))));
        var customAlerts = CreateAssignmentMenu("Custom Alerts", viewModel.AlertTones.Select(alert =>
            CreateChoice(alert.Name, true, () => viewModel.AssignToolbarCustomAlert(button, alert))));
        var menu = new ContextMenu { ItemsSource = new Control[] { alerts, customAlerts, new Separator(), reset } };
        menu.Closed += (_, _) => control.ContextMenu = null;
        control.ContextMenu = menu;
        menu.Open(control);
        e.Handled = true;

        MenuItem CreateChoice(string name, bool isCustomAudio, Action assign)
        {
            var choice = new MenuItem
            {
                Header = name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = button.AssignedPresetName == name && button.IsCustomAudio == isCustomAudio
            };
            choice.Click += (_, _) => assign();
            return choice;
        }

        static MenuItem CreateAssignmentMenu(string heading, IEnumerable<MenuItem> items)
        {
            MenuItem[] choices = items.ToArray();
            return new MenuItem { Header = heading, ItemsSource = choices, IsEnabled = choices.Length > 0 };
        }
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

        await operatorCommands.ExecuteAsync(commandId).ConfigureAwait(true);
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
        if (viewer is null || configuredScrollViewers.ContainsKey(viewer))
            return;

        configuredScrollViewers.Add(viewer, []);
        viewer.TemplateApplied += (_, _) => CacheScrollBars(viewer);

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
            () => CacheScrollBars(viewer),
            DispatcherPriority.Loaded);
    }

    private void CacheScrollBars(ScrollViewer viewer)
    {
        ScrollBar[] scrollBars = viewer.GetVisualDescendants().OfType<ScrollBar>().ToArray();
        configuredScrollViewers[viewer] = scrollBars;
        foreach (ScrollBar scrollBar in scrollBars)
            scrollBar.Opacity = 0;
    }

    private void SetScrollBarOpacity(ScrollViewer viewer, double opacity)
    {
        if (!configuredScrollViewers.TryGetValue(viewer, out ScrollBar[]? scrollBars))
            return;
        foreach (ScrollBar scrollBar in scrollBars)
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
