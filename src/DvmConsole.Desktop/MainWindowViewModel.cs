using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal const double ChannelWidgetSpacing = 8;
    internal const double DefaultWidgetCanvasWidth = 900;
    private const int MaximumSubscriberCommandAuditEntries = 50;
    private const int VoiceSampleRate = 8_000;
    private const int VocoderAudioLevelWindowSamples = VoiceSampleRate;
    private const string DvmConsoleProcessingDisplay = "DVM Console processing";
    private const string AppleVoiceProcessingDisplay = "Apple voice processing";
    private const string WindowsCommunicationsProcessingDisplay = "Windows communications processing";
    private static readonly string[] WindowsAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay, WindowsCommunicationsProcessingDisplay];
    private static readonly string[] DvmConsoleAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay];
    private readonly ChannelReceiveAudioCoordinator audioCoordinator;
    private readonly ApplicationAudioBackendProvider audioBackendProvider;
    private readonly ChannelReceiveWorkQueue receiveAudioWork;
    private readonly ReceiveEpisodeCompletionCoordinator receiveEpisodeCompletion;
    private readonly ChannelReceiveWorkQueue patchSourceReceiveWork;
    private readonly UserSettingsStore userSettingsStore;
    private readonly UserSettings userSettings;
    private readonly LatestUserSettingsWriter userSettingsWriter;
    private readonly string loadedCodeplugPath;
    private CodeplugGroupState codeplugGroupState
        => CodeplugGroupStateStore.GetOrMigrate(userSettings, loadedCodeplugPath);
    private readonly string codeplugDiagnosticsText;
    private readonly ChannelTransmitCoordinator transmitCoordinator;
    private readonly DefaultAudioDeviceMonitor defaultAudioDeviceMonitor;
    private readonly LatestBooleanStateReconciler warmMicrophoneReconciler;
    private readonly ToneTransmitCoordinator toneTransmitCoordinator;
    private readonly LocalTonePlayer localTonePlayer;
    private readonly GeneratedAudioMonitor generatedAudioMonitor;
    private readonly PatchForwardingCoordinator patchForwarding;
    private readonly PatchSourceDecodeCoordinator patchSourceDecode;
    private readonly P25KeyRing? p25KeyRing;
    private readonly DmrKeyRing? dmrKeyRing;
    private readonly NxdnKeyRing? nxdnKeyRing;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly SemaphoreSlim pttStateChangeLock = new(1, 1);
    private readonly PttSettingsViewModel pttSettings;
    private readonly PttSessionController pttSession;
    private readonly HistoryRecordingWorkspace historyRecording;
    private CallHistoryStore callHistory => historyRecording.History;
    private ObservableCollection<CallRecordingMetadata> recordingEntries
        => historyRecording.RecordingEntries;
    private readonly ToneWorkspaceViewModel toneWorkspace;
    private ObservableCollection<DtmfPresetViewModel> dtmfPresets
        => toneWorkspace.MutableDtmfPresets;
    private ObservableCollection<TonePresetViewModel> tonePresets
        => toneWorkspace.MutableTonePresets;
    private ObservableCollection<ToneSequenceStepViewModel> toneSequenceSteps
        => toneWorkspace.MutableToneSequenceSteps;
    private ObservableCollection<AlertToneViewModel> alertTones
        => toneWorkspace.MutableAlertTones;
    private readonly ObservableCollection<ToolbarClockViewModel> toolbarClocks = [];
    private readonly AudioSettingsViewModel audioSettings;
    private ObservableCollection<AudioInputPresetViewModel> audioInputPresets
        => audioSettings.MutableAudioInputPresets;
    private ObservableCollection<RxAudioProcessingModeViewModel> rxAudioProcessingModes
        => audioSettings.MutableRxAudioProcessingModes;
    private ObservableCollection<AudioDeviceOptionViewModel> audioInputDevices
        => audioSettings.MutableAudioInputDevices;
    private ObservableCollection<AudioDeviceOptionViewModel> audioOutputDevices
        => audioSettings.MutableAudioOutputDevices;
    private readonly ObservableCollection<SubscriberCommandAuditEntry> subscriberCommandAudit = [];
    private readonly DebugLogWorkspace debugLogs;
    private bool verboseDiagnosticLogging;
    private readonly ObservableCollection<string> recentCodeplugPaths = [];
    private readonly ObservableCollection<WebStreamViewModel> webStreams = [];
    private readonly WebStreamPlaybackCoordinator webStreamPlayback;
    private readonly IUiDispatcher uiDispatcher;
    private readonly ReceivePresentationController receivePresentation;
    private readonly object audioLevelLogSync = new();
    private readonly Dictionary<(ChannelViewModel Channel, ChannelAudioDirection Direction), PcmLevelLogState> audioLevelLogs = [];
    private readonly ChannelAudioMeterPipeline audioMeterPipeline = new();
    private readonly ReceiveDiagnosticsReporter receiveDiagnosticsReporter = new(TimeSpan.FromSeconds(5));
    private readonly ReceivePipelineTimingReporter receivePipelineTimingReporter = new(TimeSpan.FromSeconds(5));
    private readonly ReceiveJitterEventReporter receiveJitterEventReporter = new(TimeSpan.FromSeconds(5));
    private readonly AdaptiveReceiveJitterBufferController adaptiveReceiveJitter = new();
    private readonly ReceiveJitterBufferEffectivenessTracker receiveJitterEffectiveness = new();
    private readonly ReceiveCallEpisodeTracker receiveCallEpisodes = new();
    private readonly ReceiveOutputMutePolicy receiveOutputMutePolicy = new();
    private readonly SemaphoreSlim audioReconfigurationLock = new(1, 1);
    private readonly object receiveOutputRecoverySync = new();
    private readonly HashSet<ChannelViewModel> proactiveReceiveOutputRecoveries = [];
    private readonly Dictionary<ChannelViewModel, DateTimeOffset> receiveRetryAfter = [];
    private IReadOnlyDictionary<string, RxJitterBufferSetting> receiveJitterBufferSettingsBySystem =
        new Dictionary<string, RxJitterBufferSetting>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FneConnectionState> lastConnectionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<SystemViewModel, ReceiveAudioTrafficRouter> receiveTrafficRouters;
    private readonly ConnectionChimeTracker connectionChimeTracker = new();
    private readonly ConnectionSessionController connectionSession;
    private readonly P25KeyRequestCoordinator p25KeyRequestCoordinator = new();
    private ChannelViewModel[] suspendedAudioChannels = [];
    private bool suspendedAudioKeptActive;
    private bool outputMuted;
    private bool activityCurrentZoneOnly;
    private bool activityReceiveEnabledOnly = true;
    private PatchGroupEditorViewModel? activeMultiSelectGroup;
    private readonly CallRecordingManager callRecordings;
    private readonly RecordingPlaybackCoordinator recordingPlayback;
    private readonly ConsoleSessionRuntime sessionRuntime;
    private readonly ConsoleSessionRuntime.ConsoleSessionTimer audioMeterTimer;
    private readonly AudioRuntimeSettingsTransaction audioRuntimeSettings;
    private Bitmap? userBackgroundBitmap;
    private int disposeStarted;
    private IBrush mainBackgroundBrush = new SolidColorBrush(Color.Parse("#0D1116"));
    private string statusText;
    private string audioStatusText = "RX audio disabled.";
    private string transmitStatusText = "PTT idle.";
    private IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions> receiveAudioProcessingOptions =
        new Dictionary<VocoderMode, ReceiveAudioProcessingOptions>();
    private string clockText = string.Empty;
    private bool busy;
    private bool codeplugDiagnosticsDismissed;
    private ChannelViewModel? selectedChannel;
    private SystemViewModel? selectedSystem;
    private readonly ScaleTransform uiScaleTransform;

    internal MainWindowViewModel(
        string statusText,
        IEnumerable<SystemViewModel> systems,
        IEnumerable<ZoneViewModel> zones,
        MainWindowViewModelOptions? options = null)
    {
        options ??= new MainWindowViewModelOptions();
        IP25KeyResolver? p25KeyResolver = options.P25KeyResolver;
        UserSettingsStore? userSettingsStore = options.UserSettingsStore;
        IEnumerable<GroupConfiguration>? groupDefinitions = options.GroupDefinitions;
        bool patchSourceIdPassthrough = options.PatchSourceIdPassthrough;
        Func<IReadOnlyList<string>>? serialPortProvider = options.SerialPortProvider;
        Func<string, int, IPttSource>? serialPttFactory = options.SerialPttFactory;
        IDmrKeyResolver? dmrKeyResolver = options.DmrKeyResolver;
        INxdnKeyResolver? nxdnKeyResolver = options.NxdnKeyResolver;
        string? codeplugPath = options.CodeplugPath;
        IUiDispatcher? uiDispatcher = options.UiDispatcher;
        ConsoleSessionServices? sessionServices = options.SessionServices;
        bool networkDisabledDemo = options.NetworkDisabledDemo;
        Func<ApplicationAudioConfiguration, Task>? reconfigureApplicationAudio = options.ReconfigureApplicationAudio;
        ConsoleSessionServices services = sessionServices ?? new ConsoleSessionServices();
        sessionRuntime = new ConsoleSessionRuntime(services);
        Systems = systems.ToArray();
        Zones = zones.ToArray();
        services.Connection.Register("systems", () => new ValueTask(DisposeSystemsAsync()));
        this.uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
        this.statusText = statusText;
        codeplugDiagnosticsText = statusText;
        this.networkDisabledDemo = networkDisabledDemo;
        this.userSettingsStore = userSettingsStore ?? new UserSettingsStore(UserSettingsStore.DefaultPath);
        userSettings = this.userSettingsStore.Load();
        verboseDiagnosticLogging = userSettings.VerboseLoggingEnabled ||
            VerboseDiagnosticLogging.IsEnabled;
        userSettingsWriter = new LatestUserSettingsWriter(
            this.userSettingsStore.SaveSnapshot,
            exception => DesktopCrashLog.Write("User settings persistence", exception));
        if (NormalizeHiddenAudioProcessingMode(userSettings))
            PersistUserSettings();
        audioBackendProvider = new ApplicationAudioBackendProvider(
            CreateApplicationAudioConfiguration(),
            CreateNativeAudioBackend);
        Func<ApplicationAudioConfiguration, Task> reconfigureAudio =
            reconfigureApplicationAudio ?? ReconfigureApplicationAudioAsync;
        debugLogs = new DebugLogWorkspace(
            this.uiDispatcher.CheckAccess,
            action => this.uiDispatcher.Post(action, background: true),
            () => Volatile.Read(ref disposeStarted) != 0);
        debugLogs.PropertyChanged += HandleDebugLogWorkspacePropertyChanged;
        foreach (SystemViewModel system in Systems)
            system.SetVerboseLogging(this.verboseDiagnosticLogging);
        RegisterSessionOwnership(services);
        loadedCodeplugPath = string.IsNullOrWhiteSpace(codeplugPath)
            ? string.Empty
            : Path.GetFullPath(codeplugPath);
        this.serialPortProvider = serialPortProvider ?? SerialPttSource.GetAvailablePortNames;
        historyRecording = new HistoryRecordingWorkspace(
            userSettings.RecordingRetentionDays.ToString(CultureInfo.InvariantCulture),
            GetDefaultRecordingRoot(userSettings.RecordingRootPath));
        historyRecording.PropertyChanged += HandleHistoryRecordingPropertyChanged;
        audioSettings = new AudioSettingsViewModel(
            userSettings,
            ToAudioProcessingModeDisplay(userSettings.AudioProcessingMode));
        audioSettings.PropertyChanged += HandleAudioSettingsPropertyChanged;
        toneWorkspace = new ToneWorkspaceViewModel(userSettings);
        toneWorkspace.PropertyChanged += HandleToneWorkspacePropertyChanged;
        uiScaleTransform = new ScaleTransform
        {
            ScaleX = userSettings.UiScale,
            ScaleY = userSettings.UiScale
        };
        foreach (string path in userSettings.RecentCodeplugPaths.Take(UserSettings.MaximumRecentCodeplugs))
            recentCodeplugPaths.Add(path);
        LoadUserBackground(userSettings.UserBackgroundImage);
        ApplyTheme(userSettings.DarkMode);
        bool initialSerialPttEnabled = userSettings.SerialPttEnabled;
        string initialSerialPttPortName = userSettings.SerialPttPortName;
        int initialSerialPttBaudRate = userSettings.SerialPttBaudRate;
        string? environmentSerialPort = Environment.GetEnvironmentVariable("DVM_PTT_SERIAL_PORT");
        if (initialSerialPttPortName.Length == 0 && !string.IsNullOrWhiteSpace(environmentSerialPort))
        {
            initialSerialPttEnabled = true;
            initialSerialPttPortName = environmentSerialPort.Trim();
            initialSerialPttBaudRate = ReadSerialPttBaudRate();
        }
        pttSettings = new PttSettingsViewModel(
            ParseGlobalPttKey(userSettings.GlobalPttKey),
            ParseGlobalPttKey(userSettings.ActiveSystemPttKey),
            userSettings.TogglePttMode,
            initialSerialPttEnabled,
            userSettings.SerialPttActiveSystemOnly,
            initialSerialPttPortName,
            initialSerialPttBaudRate);
        pttSettings.PropertyChanged += HandlePttSettingsPropertyChanged;
        pttSession = new PttSessionController(
            pttSettings,
            serialPttFactory ?? ((portName, baudRate) => new SerialPttSource(portName, baudRate)),
            GetSerialPttTargetScope);
        RefreshSerialPttDevices();
        if (SerialPttEnabled && SerialPttPortName.Length > 0)
        {
            pttSession.CreateInitialSerialSource();
            SerialPttStatusText = $"Configured for {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
        }
        clockText = FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
        sessionRuntime.StartTimer(TimeSpan.FromSeconds(1), HandleClockTick);
        audioMeterTimer = sessionRuntime.CreateTimer(
            TimeSpan.FromMilliseconds(ChannelAudioMeterPipeline.RefreshIntervalMilliseconds),
            HandleAudioMeterTick,
            startImmediately: false);
        sessionRuntime.StartTimer(TimeSpan.FromSeconds(1), HandleConnectionDiagnosticsTick);
        receiveJitterBufferSettingsBySystem = BuildReceiveJitterBufferSettingsBySystem();
        receiveAudioProcessingOptions = BuildReceiveAudioProcessingOptions();
        webStreamPlayback = new WebStreamPlaybackCoordinator(
            audioBackendProvider.CreateBackend,
            () => userSettings.AudioOutputDeviceId,
            openStream: null,
            createDecoder: null,
            getStreamOutputDeviceId: GetWebStreamOutputDeviceId,
            uiDispatcher: this.uiDispatcher);
        RestoreToolbarClocks();
        p25KeyRing = p25KeyResolver as P25KeyRing;
        dmrKeyRing = dmrKeyResolver as DmrKeyRing;
        nxdnKeyRing = nxdnKeyResolver as NxdnKeyRing;
        callRecordings = new CallRecordingManager(
            RecordingRootPathText,
            HandleRecordingFaulted,
            userSettings.RecordingRetentionDays,
            ShouldRecordSource);
        callRecordings.RecordingFinalized += HandleRecordingFinalized;
        recordingPlayback = new RecordingPlaybackCoordinator(
            audioBackendProvider.CreateBackend,
            () => userSettings.AudioOutputDeviceId,
            HandleRecordingPlaybackFaulted);
        recordingPlayback.PlaybackStateChanged += HandleRecordingPlaybackStateChanged;
        audioCoordinator = new ChannelReceiveAudioCoordinator(
            CreateReceiveAudioBackend,
            CreateReceiveVocoderBackend,
            p25KeyResolver,
            HandleDecodedSamples,
            GetChannelVolume,
            GetChannelOutputDeviceId,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver,
            getChannelBalance: GetChannelStereoBalance,
            presentationSamplesObserver: HandlePresentedReceiveSamples);
        audioCoordinator.SetReceivePlaybackEpisodeResolver(ResolveReceivePlaybackEpisode);
        audioCoordinator.OutputFailed += HandleReceiveAudioOutputFailed;
        receiveAudioWork = ChannelReceiveWorkQueue.CreateWithTiming(
            ProcessAudioAsync,
            timingObserver: (channel, timing) =>
            {
                ObserveRuntimeReceiveTiming(timing);
                HandleReceiveWorkItemTiming(channel, timing);
            },
            getJitterBufferProfile: GetReceiveJitterBufferProfile);
        receiveEpisodeCompletion = new ReceiveEpisodeCompletionCoordinator(
            receiveAudioWork,
            (channel, episodeId) => audioCoordinator.CompleteEpisodeAsync(channel, episodeId),
            callRecordings.StopStream,
            ResolveReceiveRecordingTarget);
        patchSourceReceiveWork = new ChannelReceiveWorkQueue(
            ProcessPatchSourceAsync,
            getJitterBufferProfile: GetReceiveJitterBufferProfile);
        receivePresentation = new ReceivePresentationController(
            () => Volatile.Read(ref disposeStarted) != 0,
            this.uiDispatcher.CheckAccess,
            action => this.uiDispatcher.Post(action),
            PresentSystemTraffic);
        foreach (SystemViewModel system in Systems)
            RefreshJitterBufferTelemetry(system);
        transmitCoordinator = new ChannelTransmitCoordinator(
            p25KeyResolver,
            CreateAudioInputProcessingOptions(
                userSettings.AudioInputDeviceId,
                GetConfiguredAudioProcessingMode(),
                userSettings.AudioInputAgcEnabled,
                userSettings.AudioInputAgcTargetDbfs,
                userSettings.AudioInputGain,
                userSettings.AudioInputEqLowGainDb,
                userSettings.AudioInputEqMidGainDb,
                userSettings.AudioInputEqHighGainDb),
            HandleTransmitSamples,
            CreateTransmitAudioBackend,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        audioRuntimeSettings = new AudioRuntimeSettingsTransaction(
            transmitCoordinator.SetKeepMicrophoneWarmAsync,
            reconfigureAudio);
        warmMicrophoneReconciler = new LatestBooleanStateReconciler(
            transmitCoordinator.SetKeepMicrophoneWarmAsync);
        warmMicrophoneReconciler.Reconciled += HandleWarmMicrophoneReconciled;
        if (userSettings.KeepTransmitMicrophoneWarm)
            _ = warmMicrophoneReconciler.SetDesired(true);
        toneTransmitCoordinator = new ToneTransmitCoordinator(
            p25KeyResolver,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        localTonePlayer = new LocalTonePlayer(
            CreateTransmitAudioBackend,
            () => userSettings.AudioOutputDeviceId);
        generatedAudioMonitor = new GeneratedAudioMonitor(
            CreateTransmitAudioBackend,
            () => userSettings.AudioOutputDeviceId);
        receiveTrafficRouters = Systems.ToDictionary(
            system => system,
            system => new ReceiveAudioTrafficRouter(
                system.Channels
                    .GroupBy(channel => (ProtocolFor(channel), channel.Definition.DestinationId))
                    .ToDictionary(group => group.Key, group => group.ToArray())));
        RestoreChannelPresentation();
        GroupConfiguration[] configuredGroups = (groupDefinitions ?? []).ToArray();
        patchForwarding = new PatchForwardingCoordinator(
            Systems,
            p25KeyResolver,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver,
            diagnosticObserver: HandlePatchForwardingDiagnostic)
        {
            SourceIdPassthrough = patchSourceIdPassthrough
        };
        patchSourceDecode = new PatchSourceDecodeCoordinator(
            p25KeyResolver,
            ObservePatchDecodedSamples,
            createVocoderBackend: CreateReceiveVocoderBackend,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        RestorePatchState(configuredGroups);
        PatchGroups = BuildPatchGroups(configuredGroups);
        RefreshPatchMembershipConflicts();
        ToolbarClocks = new ReadOnlyObservableCollection<ToolbarClockViewModel>(toolbarClocks);
        SubscriberCommandAudit = new ReadOnlyObservableCollection<SubscriberCommandAuditEntry>(subscriberCommandAudit);
        RecentCodeplugPaths = new ReadOnlyObservableCollection<string>(recentCodeplugPaths);
        WebStreams = new ReadOnlyObservableCollection<WebStreamViewModel>(webStreams);
        ConfigureWebStreams();
        TaskObservation.Observe(RestoreSelectedWebStreamsAsync());
        RefreshRecordings(pruneExpired: true);
        ConfigureChannels();
        SubscribeToSystems();
        RestoreInitialSelection();
        RefreshActivityCallHistory();

        connectionSession = new ConnectionSessionController(
            Systems,
            SyncPatchSourceDecodeAsync,
            () => patchSourceDecode.StopAllAsync(),
            patchForwarding.StopAll,
            SetBusy,
            text => StatusText = text,
            system => SelectedSystem = system,
            HandleSystemStatus);
        ConnectCommand = new AsyncRelayCommand(
            connectionSession.ConnectAsync,
            () => !this.networkDisabledDemo && !busy && Systems.Count > 0);
        DisconnectCommand = new AsyncRelayCommand(connectionSession.DisconnectAsync, () => !busy && Systems.Count > 0);
        ToggleSelectedSystemOutputMuteCommand = new AsyncRelayCommand(
            ToggleSelectedSystemOutputMuteAsync,
            () => SelectedSystem is not null);
        ToggleSelectedZoneOutputMuteCommand = new AsyncRelayCommand(
            ToggleSelectedZoneOutputMuteAsync,
            () => SelectedSystem?.SelectedZone is not null);
        SendDtmfCommand = new AsyncRelayCommand(SendDtmfAsync, CanSendGeneratedAudio);
        SendToneCommand = new AsyncRelayCommand(SendToneAsync, CanSendGeneratedAudio);
        SaveDtmfPresetCommand = new RelayCommand(SaveDtmfPreset);
        SaveTonePresetCommand = new RelayCommand(SaveTonePreset);
        ApplyAudioInputSettingsCommand = new AsyncRelayCommand(
            () => ApplyAudioInputSettingsAsync(restartActiveAudio: true),
            () => !busy && transmitCoordinator.ActiveChannel is null,
            HandleAudioCommandFault);
        ApplyRxAudioProcessingOptionsCommand = new AsyncRelayCommand(
            ApplyRxAudioProcessingOptionsAsync,
            () => !busy,
            HandleAudioCommandFault);
        ApplyRecordingRetentionCommand = new RelayCommand(ApplyRecordingRetention);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        defaultAudioDeviceMonitor = new DefaultAudioDeviceMonitor(
            new AudioBackendDeviceTopologyProvider(CreateReceiveAudioBackend),
            HandleAudioDeviceTopologyChangedAsync);
        if (networkDisabledDemo)
        {
            InstallDemoAudioDevices();
        }
        else
        {
            RefreshAudioDevices();
            defaultAudioDeviceMonitor.Start();
        }
        transmitCoordinator.Faulted += HandleTransmitFaulted;
        pttSession.StateChanged += HandlePttSourceStateChanged;
        pttSession.AttachEvents();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public bool IsCodeplugLoaded => Systems.Count > 0;

    public string? CurrentCodeplugPath => loadedCodeplugPath.Length == 0 ? null : loadedCodeplugPath;

    public string SettingsVersionText => userSettings.SchemaVersion == UserSettings.CurrentSchemaVersion
        ? $"Profile format v{userSettings.SchemaVersion}"
        : userSettings.SchemaVersion > UserSettings.CurrentSchemaVersion
            ? $"Profile format v{userSettings.SchemaVersion} (newer than this build)"
            : $"Profile format v{userSettings.SchemaVersion} (legacy)";

    internal WindowPlacementSetting MainWindowPlacement => new()
    {
        Left = userSettings.MainWindowPlacement.Left,
        Top = userSettings.MainWindowPlacement.Top,
        Width = userSettings.MainWindowPlacement.Width,
        Height = userSettings.MainWindowPlacement.Height
    };

    internal void SaveMainWindowPlacement(WindowPlacementSetting placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        userSettings.MainWindowPlacement = new WindowPlacementSetting
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height
        };
        PersistUserSettings();
    }

    public ReadOnlyObservableCollection<string> RecentCodeplugPaths { get; }

    public IReadOnlyList<string> NamedSettingsProfiles => userSettingsStore.ListNamedProfiles();

    public bool HasCodeplugDiagnostics => !codeplugDiagnosticsDismissed &&
        (!IsCodeplugLoaded || codeplugDiagnosticsText.Contains('\n'));

    public string CodeplugDiagnosticsText => codeplugDiagnosticsText;

    public void DismissCodeplugDiagnostics()
    {
        if (codeplugDiagnosticsDismissed)
            return;

        codeplugDiagnosticsDismissed = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCodeplugDiagnostics)));
    }

    public bool ShowCallHistoryPane
    {
        get => userSettings.ShowCallHistoryPane;
        set
        {
            if (userSettings.ShowCallHistoryPane == value)
                return;
            userSettings.ShowCallHistoryPane = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCallHistoryPane)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActivitySidebarCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySidebarWidth)));
        }
    }

    public bool IsActivitySidebarCollapsed => !ShowCallHistoryPane;

    public double ActivitySidebarWidth => ShowCallHistoryPane ? 250 : 34;

    public bool ShowSystemStatus
    {
        get => userSettings.ShowSystemStatus;
        set
        {
            if (userSettings.ShowSystemStatus == value)
                return;
            userSettings.ShowSystemStatus = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSystemStatus)));
        }
    }

    public double UiFontSize
    {
        get => userSettings.UiFontSize;
        set
        {
            double normalized = Math.Clamp(value, 11, 20);
            if (Math.Abs(userSettings.UiFontSize - normalized) < 0.001)
                return;
            userSettings.UiFontSize = normalized;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiFontSizeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiSmallFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiCompactFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiHeadingFontSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelCardHeight)));
            if (userSettings.ChannelWidgetPositions.Count == 0)
                ApplyDefaultChannelWidgetLayout();
            foreach (ZoneViewModel zone in Zones)
            {
                zone.SetWidgetCardHeight(ChannelCardHeight);
                zone.RefreshWidgetCanvasBounds();
            }
        }
    }

    public string UiFontSizeText => $"Text size: {UiFontSize:0}";
    public double UiSmallFontSize => UiFontSize - 2;
    public double UiCompactFontSize => UiFontSize - 3;
    public double UiHeadingFontSize => UiFontSize + 4;
    public double ChannelCardHeight => 122 + ((UiFontSize - 14) * 3);

    public double UiScale
    {
        get => userSettings.UiScale;
        set
        {
            double normalized = Math.Clamp(value, 0.75, 1.5);
            if (Math.Abs(userSettings.UiScale - normalized) < 0.001)
                return;
            userSettings.UiScale = normalized;
            uiScaleTransform.ScaleX = normalized;
            uiScaleTransform.ScaleY = normalized;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiScale)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiScaleText)));
        }
    }

    public string UiScaleText => $"Interface scale: {UiScale * 100:0}%";
    public ScaleTransform UiScaleTransform => uiScaleTransform;

    public bool ShowChannels
    {
        get => userSettings.ShowChannels;
        set
        {
            if (userSettings.ShowChannels == value)
                return;
            userSettings.ShowChannels = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowChannels)));
        }
    }

    public bool ShowAlertTones
    {
        get => userSettings.ShowAlertTones;
        set
        {
            if (userSettings.ShowAlertTones == value)
                return;
            userSettings.ShowAlertTones = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAlertTones)));
        }
    }

    public bool LockWidgets
    {
        get => userSettings.LockWidgets;
        set
        {
            if (userSettings.LockWidgets == value)
                return;
            userSettings.LockWidgets = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockWidgets)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResizeLayout)));
        }
    }

    public IBrush MainBackgroundBrush => mainBackgroundBrush;

    public bool CanResizeLayout => !userSettings.LockWidgets;

    public string? UserBackgroundImage => userSettings.UserBackgroundImage;

    public bool SetUserBackground(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The background image was not found.", fullPath);

            Bitmap bitmap = new(fullPath);
            userBackgroundBitmap?.Dispose();
            userBackgroundBitmap = bitmap;
            mainBackgroundBrush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.22
            };
            userSettings.UserBackgroundImage = fullPath;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserBackgroundImage)));
            StatusText = $"Background loaded: {Path.GetFileName(fullPath)}.";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = $"Background unavailable: {exception.Message}";
            return false;
        }
    }

    public void ClearUserBackground()
    {
        userBackgroundBitmap?.Dispose();
        userBackgroundBitmap = null;
        mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
        userSettings.UserBackgroundImage = null;
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserBackgroundImage)));
        StatusText = "User background cleared.";
    }

    public void ResetLayout()
    {
        userSettings.ShowSystemStatus = true;
        userSettings.ShowChannels = true;
        userSettings.ShowAlertTones = true;
        userSettings.LockWidgets = true;
        userSettings.ShowCallHistoryPane = true;
        userSettings.ChannelWidgetPositions.Clear();
        ApplyDefaultChannelWidgetLayout();
        PersistUserSettings();
        foreach (string propertyName in new[]
                 {
                     nameof(ShowSystemStatus),
                     nameof(ShowChannels),
                     nameof(ShowAlertTones),
                     nameof(LockWidgets),
                     nameof(CanResizeLayout),
                     nameof(ShowCallHistoryPane)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        StatusText = "Channel widgets reset to their default positions and locked.";
    }

    public void MoveChannelWidget(ChannelViewModel channel, double x, double y, bool persist)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (userSettings.LockWidgets)
            return;

        channel.SetWidgetPosition(x, y);
        if (!persist)
            return;

        userSettings.ChannelWidgetPositions[channel.SettingsKey] = new WidgetPositionSetting
        {
            X = channel.WidgetX,
            Y = channel.WidgetY
        };
        PersistUserSettings();
        StatusText = $"Moved {channel.Name} to {channel.WidgetX:0}, {channel.WidgetY:0}.";
    }

    public void ExportSettings(string path)
        => userSettingsStore.Export(userSettings, path);

    public SettingsImportPreview PreviewSettingsImport(string path)
        => userSettingsStore.PreviewImport(path);

    public SettingsImportPreview PreviewNamedSettingsProfile(string profileName)
        => userSettingsStore.PreviewNamedProfile(profileName);

    public void ImportSettings(string path, SettingsImportScope scope = SettingsImportScope.All)
        => userSettingsStore.Import(path, scope);

    public void SaveNamedSettingsProfile(string profileName)
    {
        userSettingsStore.SaveNamedProfile(profileName, userSettings);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamedSettingsProfiles)));
        StatusText = $"Settings profile '{profileName.Trim()}' saved.";
    }

    public void ImportNamedSettingsProfile(
        string profileName,
        SettingsImportScope scope = SettingsImportScope.OperatorState)
    {
        userSettingsStore.ImportNamedProfile(profileName, scope);
        StatusText = $"Settings profile '{profileName.Trim()}' imported.";
    }

    public void DeleteNamedSettingsProfile(string profileName)
    {
        userSettingsStore.DeleteNamedProfile(profileName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamedSettingsProfiles)));
        StatusText = $"Settings profile '{profileName.Trim()}' deleted.";
    }

    public void ResetSettings()
        => userSettingsStore.Reset();

    public void ClearCallHistory()
    {
        callHistory.Clear();
        NotifyCallHistoryChanged();
        StatusText = "Activity history cleared.";
    }

    public void AddEventHistory(
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        void Apply()
        {
            callHistory.AddEvent(DateTimeOffset.Now, source, message, ridText, tgidText);
            NotifyCallHistoryChanged();
        }

        if (uiDispatcher.CheckAccess())
            Apply();
        else
            uiDispatcher.Post(Apply);
    }

    public void ExportCallHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string>
        {
            "Start,End,DurationSeconds,System,Channel,SourceId,Caller,Talkgroup,Protocol,Encryption,StreamId"
        };
        lines.AddRange(CallHistory.Select(entry => string.Join(",",
            Csv(entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            Csv(entry.EndTimestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
            Csv(entry.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty),
            Csv(entry.SystemName),
            Csv(entry.DisplayChannelText),
            Csv(entry.DisplaySourceText),
            Csv(entry.CallerText),
            Csv(entry.DisplayDestinationText),
            Csv(entry.ProtocolText),
            Csv(entry.EncryptionText),
            entry.StreamId.ToString(CultureInfo.InvariantCulture))));
        File.WriteAllLines(fullPath, lines);
        StatusText = $"Exported {CallHistory.Count} activity-history entr{(CallHistory.Count == 1 ? "y" : "ies")}.";
    }

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    public string AudioStatusText
    {
        get => audioStatusText;
        private set => SetField(ref audioStatusText, value);
    }

    public string TransmitStatusText
    {
        get => transmitStatusText;
        private set => SetField(ref transmitStatusText, value);
    }

    public string DtmfDigits
    {
        get => toneWorkspace.DtmfDigits;
        set => toneWorkspace.DtmfDigits = value;
    }

    public string ToneFrequencyText
    {
        get => toneWorkspace.ToneFrequencyText;
        set => toneWorkspace.ToneFrequencyText = value;
    }

    public string ToneDurationText
    {
        get => toneWorkspace.ToneDurationText;
        set => toneWorkspace.ToneDurationText = value;
    }

    public string AudioInputDeviceIdText
    {
        get => audioSettings.AudioInputDeviceIdText;
        set => audioSettings.AudioInputDeviceIdText = value;
    }

    public string AudioOutputDeviceIdText
    {
        get => audioSettings.AudioOutputDeviceIdText;
        set => audioSettings.AudioOutputDeviceIdText = value;
    }

    public AudioDeviceOptionViewModel? SelectedAudioInputDevice
    {
        get => audioSettings.SelectedAudioInputDevice;
        set
        {
            if (ReferenceEquals(audioSettings.SelectedAudioInputDevice, value))
                return;
            audioSettings.SelectedAudioInputDevice = value;
            RefreshAppleVoiceProcessingRouteState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MicrophoneInputSourceText)));
        }
    }

    public AudioDeviceOptionViewModel? SelectedAudioOutputDevice
    {
        get => audioSettings.SelectedAudioOutputDevice;
        set
        {
            if (ReferenceEquals(audioSettings.SelectedAudioOutputDevice, value))
                return;
            audioSettings.SelectedAudioOutputDevice = value;
            RefreshAppleVoiceProcessingRouteState();
        }
    }

    public string AudioInputGainText
    {
        get => audioSettings.AudioInputGainText;
        set => audioSettings.AudioInputGainText = value;
    }

    public string AudioInputLowGainText
    {
        get => audioSettings.AudioInputLowGainText;
        set => audioSettings.AudioInputLowGainText = value;
    }

    public string AudioInputMidGainText
    {
        get => audioSettings.AudioInputMidGainText;
        set => audioSettings.AudioInputMidGainText = value;
    }

    public string AudioInputHighGainText
    {
        get => audioSettings.AudioInputHighGainText;
        set => audioSettings.AudioInputHighGainText = value;
    }

    public bool AudioInputAgcEnabled
    {
        get => audioSettings.AudioInputAgcEnabled;
        set
        {
            if (audioSettings.AudioInputAgcEnabled == value)
                return;
            audioSettings.AudioInputAgcEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgcTargetEnabled)));
        }
    }

    public string AudioInputAgcTargetDbfsText
    {
        get => audioSettings.AudioInputAgcTargetDbfsText;
        set => audioSettings.AudioInputAgcTargetDbfsText = value;
    }

    public bool HighQualityBluetoothAudioEnabled
    {
        get => audioSettings.HighQualityBluetoothAudioEnabled;
        set => audioSettings.HighQualityBluetoothAudioEnabled = value;
    }

    public bool IsHighQualityBluetoothAudioAvailable
        => OperatingSystem.IsMacOSVersionAtLeast(26);

    public bool KeepTransmitMicrophoneWarm
    {
        get => userSettings.KeepTransmitMicrophoneWarm;
        set
        {
            if (userSettings.KeepTransmitMicrophoneWarm == value)
                return;
            userSettings.KeepTransmitMicrophoneWarm = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepTransmitMicrophoneWarm)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepTransmitMicrophoneWarmToolTip)));
            _ = warmMicrophoneReconciler.SetDesired(value);
        }
    }

    public string KeepTransmitMicrophoneWarmToolTip
        => KeepTransmitMicrophoneWarm
            ? "Keep transmit microphone warm: On (click to turn off)"
            : "Keep transmit microphone warm: Off (click to turn on)";

    public bool OutputMuted
    {
        get => outputMuted;
        set
        {
            if (outputMuted == value)
                return;

            audioCoordinator.SetOutputMuted(value);
            outputMuted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputMuted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputMuteGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputMuteToolTip)));
            AudioStatusText = value
                ? "Live RX output is muted; decoding, call state, and TAR recording continue."
                : "Live RX output is restored.";
        }
    }

    public string OutputMuteGlyph => OutputMuted ? "🔇" : "🔊";

    public string OutputMuteToolTip
        => OutputMuted
            ? "Live RX output muted; TAR continues (click to restore output)"
            : "Mute live RX output to the selected output device; TAR continues";

    public bool SelectedSystemOutputMuted
        => SelectedSystem is not null && receiveOutputMutePolicy.IsMuted(SelectedSystem);

    public bool SelectedZoneOutputMuted
        => SelectedSystem?.SelectedZone is ZoneViewModel zone && receiveOutputMutePolicy.IsMuted(zone);

    public string SelectedSystemOutputMuteGlyph => SelectedSystemOutputMuted ? "S🔇" : "S🔊";
    public string SelectedZoneOutputMuteGlyph => SelectedZoneOutputMuted ? "Z🔇" : "Z🔊";

    public string SelectedSystemOutputMuteToolTip
        => SelectedSystemOutputMuted
            ? $"Restore live RX output for {SelectedSystem?.Name}; TAR continues"
            : $"Mute live RX output for {SelectedSystem?.Name ?? "the selected system"}; TAR continues";

    public string SelectedZoneOutputMuteToolTip
        => SelectedZoneOutputMuted
            ? $"Restore live RX output for zone {SelectedSystem?.SelectedZone?.Name}; TAR continues"
            : $"Mute live RX output for zone {SelectedSystem?.SelectedZone?.Name ?? "the selected zone"}; TAR continues";

    public IReadOnlyList<string> AudioProcessingModeOptions
        => OperatingSystem.IsWindows()
            ? WindowsAudioProcessingModeOptions
            : DvmConsoleAudioProcessingModeOptions;

    public bool IsAppleVoiceProcessingPlatformAvailable
        => OperatingSystem.IsMacOS();

    public bool IsMacOsPermissionRequestAvailable
        => OperatingSystem.IsMacOS();

    public void RequestMacOsKeyboardPermission()
    {
        try
        {
            MacOsPermissionRequestResult result = MacOsPrivacyPermissionRequester.RequestKeyboardAccess();
            AudioStatusText = result switch
            {
                MacOsPermissionRequestResult.Granted => "macOS keyboard access is already granted.",
                MacOsPermissionRequestResult.Requested =>
                    "macOS keyboard access requested. Approve the prompt, or enable DVM Console under System Settings > Privacy & Security > Input Monitoring.",
                _ => "macOS keyboard access is unavailable on this platform."
            };
        }
        catch (Exception exception)
        {
            AudioStatusText = $"Unable to request macOS keyboard access: {exception.Message}";
        }
    }

    public void RequestMacOsMicrophonePermission()
    {
        try
        {
            MacOsPermissionRequestResult result = MacOsPrivacyPermissionRequester.RequestMicrophoneAccess();
            AudioStatusText = result switch
            {
                MacOsPermissionRequestResult.Granted => "macOS microphone access is already granted.",
                MacOsPermissionRequestResult.Requested =>
                    "macOS microphone access requested. Approve the system prompt to enable transmit audio.",
                MacOsPermissionRequestResult.Denied =>
                    "macOS microphone access is denied. Enable DVM Console under System Settings > Privacy & Security > Microphone.",
                MacOsPermissionRequestResult.Restricted =>
                    "macOS microphone access is restricted by system policy.",
                _ => "macOS microphone access is unavailable on this platform."
            };
        }
        catch (Exception exception)
        {
            AudioStatusText = $"Unable to request macOS microphone access: {exception.Message}";
        }
    }

    public bool IsAppleVoiceProcessingRouteCompatible
        => IsAppleVoiceProcessingDevicePairCompatible(SelectedAudioInputDevice, SelectedAudioOutputDevice);

    public string AppleVoiceProcessingRouteDescription
        => IsAppleVoiceProcessingRouteCompatible
            ? "Apple voice processing supports the system-default input/output pair or one duplex device selected for both input and output."
            : "Apple voice processing is unavailable for this device combination. Choose the system-default input and output, or the same duplex device for both.";

    public string SelectedAudioProcessingMode
    {
        get => audioSettings.SelectedAudioProcessingMode;
        set
        {
            string normalized = value switch
            {
                WindowsCommunicationsProcessingDisplay when OperatingSystem.IsWindows() =>
                    WindowsCommunicationsProcessingDisplay,
                _ => DvmConsoleProcessingDisplay
            };
            if (audioSettings.SelectedAudioProcessingMode == normalized)
                return;
            audioSettings.SelectedAudioProcessingMode = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDvmConsoleProcessingSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgcTargetEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioProcessingDescription)));
        }
    }

    public bool IsDvmConsoleProcessingSelected
        => SelectedAudioProcessingMode == DvmConsoleProcessingDisplay;

    public bool IsAgcTargetEnabled
        => IsDvmConsoleProcessingSelected && AudioInputAgcEnabled;

    public string AudioProcessingDescription
        => SelectedAudioProcessingMode switch
        {
            AppleVoiceProcessingDisplay =>
                "Apple Voice Processing uses one coordinated full-duplex route for application playback and transmit capture, providing the far-end reference needed for acoustic echo cancellation. RX vocoder processing is controlled separately.",
            WindowsCommunicationsProcessingDisplay =>
                "Windows requests the selected endpoint's communications processing for transmit capture. Actual AEC, noise suppression, and AGC depend on Windows, the audio driver, and the endpoint. DVM Console gain, EQ, and AGC are bypassed.",
            _ => "DVM Console applies its gain, EQ, and optional AGC after microphone capture."
        };

    public string AudioInputPresetNameText
    {
        get => audioSettings.AudioInputPresetNameText;
        set => audioSettings.AudioInputPresetNameText = value;
    }

    public bool MuteRxAudioWhileTransmitting
    {
        get => userSettings.MuteRxAudioWhileTransmitting;
        set
        {
            if (userSettings.MuteRxAudioWhileTransmitting == value)
                return;
            userSettings.MuteRxAudioWhileTransmitting = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MuteRxAudioWhileTransmitting)));
        }
    }

    public bool TalkPermitTone
    {
        get => userSettings.TalkPermitTone;
        set
        {
            if (userSettings.TalkPermitTone == value)
                return;
            userSettings.TalkPermitTone = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TalkPermitTone)));
        }
    }

    public bool ConnectionChimes
    {
        get => userSettings.ConnectionChimes;
        set
        {
            if (userSettings.ConnectionChimes == value)
                return;
            userSettings.ConnectionChimes = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionChimes)));
        }
    }

    public bool VerboseLoggingEnabled
    {
        get => verboseDiagnosticLogging;
        set
        {
            if (verboseDiagnosticLogging == value)
                return;
            verboseDiagnosticLogging = value;
            userSettings.VerboseLoggingEnabled = value;
            lock (audioLevelLogSync)
                audioLevelLogs.Clear();
            foreach (SystemViewModel system in Systems)
                system.SetVerboseLogging(value);
            PersistUserSettings();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(VerboseLoggingEnabled)));
        }
    }

    public bool DarkMode
    {
        get => userSettings.DarkMode;
        set
        {
            if (userSettings.DarkMode == value)
                return;
            userSettings.DarkMode = value;
            ApplyTheme(value);
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
                channel.SetDarkMode(value);
            foreach (ZoneViewModel zone in Zones)
                zone.SetDarkMode(value);
            if (userBackgroundBitmap is null)
            {
                mainBackgroundBrush = CreateShellBackgroundBrush(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
            }
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DarkMode)));
        }
    }

    public string ClockText => clockText;

    public bool ClockUse24HourTime
    {
        get => userSettings.ClockUse24HourTime;
        set
        {
            if (userSettings.ClockUse24HourTime == value)
                return;
            userSettings.ClockUse24HourTime = value;
            RefreshClock();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClockUse24HourTime)));
        }
    }

    public bool ClockShowSeconds
    {
        get => userSettings.ClockShowSeconds;
        set
        {
            if (userSettings.ClockShowSeconds == value)
                return;
            userSettings.ClockShowSeconds = value;
            RefreshClock();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClockShowSeconds)));
        }
    }

    public bool SaveToolbarClocks()
    {
        List<ToolbarClockSetting> settings = [];
        foreach (ToolbarClockViewModel clock in toolbarClocks)
        {
            if (!clock.TryGetUtcOffset(out _))
            {
                StatusText = $"{clock.SlotLabel} must use a UTC offset from -12 to +14.";
                return false;
            }
            settings.Add(clock.ToSetting());
        }

        userSettings.ToolbarClocks = settings;
        PersistUserSettings();
        RefreshClock();
        StatusText = $"Saved {settings.Count(clock => clock.Enabled)} toolbar clock(s).";
        return true;
    }

    public bool KeepWindowOnTop
    {
        get => userSettings.KeepWindowOnTop;
        set
        {
            if (userSettings.KeepWindowOnTop == value)
                return;
            userSettings.KeepWindowOnTop = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepWindowOnTop)));
        }
    }

    public bool TogglePttMode
    {
        get => pttSettings.TogglePttMode;
        set
        {
            if (pttSettings.TogglePttMode == value)
                return;
            userSettings.TogglePttMode = value;
            pttSession.SetToggleMode(value);
            PersistUserSettings();
            pttSettings.TogglePttMode = value;
        }
    }

    public string GlobalPttKeyText => pttSession.GlobalKey == KeyboardPttKey.None
        ? "Keyboard PTT disabled"
        : pttSession.GlobalKey.ToString();

    public IReadOnlyList<KeyboardPttKey> GlobalPttKeyOptions => pttSettings.GlobalPttKeyOptions;

    public KeyboardPttKey SelectedGlobalPttKey
    {
        get => pttSettings.SelectedGlobalPttKey;
        set => pttSettings.SelectedGlobalPttKey = value;
    }

    public Task ApplyGlobalPttKeySelectionAsync()
        => SetGlobalPttKeyAsync(SelectedGlobalPttKey);

    public string ActiveSystemPttKeyText =>
        pttSession.ActiveSystemKey == KeyboardPttKey.None
            ? "Keyboard PTT disabled"
            : pttSession.ActiveSystemKey.ToString();

    public KeyboardPttKey SelectedActiveSystemPttKey
    {
        get => pttSettings.SelectedActiveSystemPttKey;
        set => pttSettings.SelectedActiveSystemPttKey = value;
    }

    public Task ApplyActiveSystemPttKeySelectionAsync()
        => SetActiveSystemPttKeyAsync(SelectedActiveSystemPttKey);

    public bool SerialPttEnabled
    {
        get => pttSettings.SerialPttEnabled;
        set => pttSettings.SerialPttEnabled = value;
    }

    public bool SerialPttActiveSystemOnly
    {
        get => pttSettings.SerialPttActiveSystemOnly;
        set => pttSettings.SerialPttActiveSystemOnly = value;
    }

    public string SerialPttPortName
    {
        get => pttSettings.SerialPttPortName;
        set => pttSettings.SerialPttPortName = value;
    }

    public int SerialPttBaudRate
    {
        get => pttSettings.SerialPttBaudRate;
        set => pttSettings.SerialPttBaudRate = value;
    }

    public IReadOnlyList<string> SerialPttPortOptions => pttSettings.SerialPttPortOptions;

    public IReadOnlyList<int> SerialPttBaudRates
        => pttSettings.SerialPttBaudRates;

    public string SerialPttStatusText
    {
        get => pttSettings.SerialPttStatusText;
        private set => pttSettings.SerialPttStatusText = value;
    }

    public void RefreshSerialPttDevices()
    {
        try
        {
            string[] devices = serialPortProvider()
                .Where(portName => !string.IsNullOrWhiteSpace(portName))
                .Select(portName => portName.Trim())
                .Append(SerialPttPortName)
                .Where(portName => portName.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(portName => portName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            pttSettings.ReplaceSerialPttPortOptions(devices);
            if (SerialPttPortName.Length == 0 && devices.Length > 0)
                SerialPttPortName = devices[0];
            pttSettings.NotifySerialPttPortOptionsChanged();
            SerialPttStatusText = pttSession.HasSerialSource && SerialPttEnabled
                ? $"Serial PTT configured for {SerialPttPortName} at {SerialPttBaudRate:N0} baud."
                : devices.Length == 0
                    ? "Serial PTT is disabled; no serial devices were detected."
                    : $"Serial PTT is disabled; detected {devices.Length} serial device(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            pttSettings.ReplaceSerialPttPortOptions(
                SerialPttPortName.Length > 0 ? [SerialPttPortName] : []);
            pttSettings.NotifySerialPttPortOptionsChanged();
            SerialPttStatusText = $"Serial device discovery unavailable: {exception.Message}";
        }
    }

    public async Task<bool> ApplySerialPttSettingsAsync()
    {
        string portName = SerialPttPortName.Trim();
        int baudRate = SerialPttBaudRate;
        if (SerialPttEnabled && portName.Length == 0)
        {
            SerialPttStatusText = "Select a serial device before enabling hardware PTT.";
            return false;
        }
        if (baudRate is < 300 or > 4_000_000)
        {
            SerialPttStatusText = "Serial PTT baud rate must be between 300 and 4,000,000.";
            return false;
        }

        try
        {
            await pttSession.ReplaceSerialSourceAsync(
                SerialPttEnabled,
                portName,
                baudRate,
                () =>
                {
                    userSettings.SerialPttEnabled = SerialPttEnabled;
                    userSettings.SerialPttActiveSystemOnly = SerialPttActiveSystemOnly;
                    userSettings.SerialPttPortName = portName;
                    userSettings.SerialPttBaudRate = baudRate;
                    PersistUserSettings();
                }).ConfigureAwait(false);
            if (!SerialPttEnabled)
            {
                SerialPttStatusText = "Serial PTT is disabled.";
                TransmitStatusText = "PTT idle; serial hardware source disabled.";
                return true;
            }

            SerialPttStatusText = pttSession.IsStarted
                ? $"Serial PTT ready on {portName} at {baudRate:N0} baud."
                : $"Serial PTT configured for {portName} at {baudRate:N0} baud.";
            TransmitStatusText = pttSession.IsStarted
                ? $"PTT idle; serial source {portName} ready for {SerialPttScopeText}."
                : $"PTT idle; serial source {portName} will start for {SerialPttScopeText}.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            SerialPttStatusText = $"Serial PTT unavailable on {portName}: {exception.Message}";
            TransmitStatusText = $"PTT idle; serial source unavailable: {exception.Message}";
            return false;
        }
    }

    public bool RestoreSelectedChannelsOnStartup
    {
        get => userSettings.RestoreSelectedChannelsOnStartup;
        set
        {
            if (userSettings.RestoreSelectedChannelsOnStartup == value)
                return;
            userSettings.RestoreSelectedChannelsOnStartup = value;
            if (!value)
                userSettings.SelectedWebStreams.Clear();
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreSelectedChannelsOnStartup)));
        }
    }

    public string DtmfPresetName
    {
        get => toneWorkspace.DtmfPresetName;
        set => toneWorkspace.DtmfPresetName = value;
    }

    public string TonePresetName
    {
        get => toneWorkspace.TonePresetName;
        set => toneWorkspace.TonePresetName = value;
    }

    public string QuickCallToneAText
    {
        get => toneWorkspace.QuickCallToneAText;
        set => toneWorkspace.QuickCallToneAText = value;
    }

    public string QuickCallToneBText
    {
        get => toneWorkspace.QuickCallToneBText;
        set => toneWorkspace.QuickCallToneBText = value;
    }

    public string AlertToneNameText
    {
        get => toneWorkspace.AlertToneNameText;
        set => toneWorkspace.AlertToneNameText = value;
    }

    public string RecordingRetentionDaysText
    {
        get => historyRecording.RecordingRetentionDaysText;
        set => historyRecording.RecordingRetentionDaysText = value;
    }

    public string RecordingRootPathText
    {
        get => historyRecording.RecordingRootPathText;
        set => historyRecording.RecordingRootPathText = value;
    }

    public string SelectionStatusText => networkDisabledDemo
        ? "Demo input: local pointer · HOLD mode · network output disabled"
        : selectedChannel is null
        ? $"Choose TX on one or more cards. Global PTT: {GlobalPttKeyText}. Active-system PTT: {ActiveSystemPttKeyText}."
        : $"RX focus: {selectedChannel.Name}. Global PTT: {GlobalPttKeyText}. Active-system PTT: {ActiveSystemPttKeyText}.";

    public IReadOnlyList<SystemViewModel> Systems { get; }
    public IReadOnlyList<KeyStatusItemViewModel> KeyStatusItems
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.Definition.IsEncrypted)
            .Select(channel => KeyStatusItemViewModel.From(channel, p25KeyRing, dmrKeyRing, nxdnKeyRing))
            .ToArray();
    public bool HasNoKeyStatusItems => KeyStatusItems.Count == 0;
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    public IReadOnlyList<string> PatchGroupNames => patchForwarding.GroupNames;
    public IReadOnlyList<PatchGroupEditorViewModel> PatchGroups { get; }
    public ReadOnlyObservableCollection<DtmfPresetViewModel> DtmfPresets
        => toneWorkspace.DtmfPresets;
    public ReadOnlyObservableCollection<TonePresetViewModel> TonePresets
        => toneWorkspace.TonePresets;
    public ReadOnlyObservableCollection<ToneSequenceStepViewModel> ToneSequenceSteps
        => toneWorkspace.ToneSequenceSteps;
    public ReadOnlyObservableCollection<AlertToneViewModel> AlertTones
        => toneWorkspace.AlertTones;
    public ReadOnlyObservableCollection<BuiltInAlertToneViewModel> BuiltInAlertTones
        => toneWorkspace.BuiltInAlertTones;
    public ReadOnlyObservableCollection<ToolbarClockViewModel> ToolbarClocks { get; }
    public ReadOnlyObservableCollection<AudioInputPresetViewModel> AudioInputPresets
        => audioSettings.AudioInputPresets;
    public ReadOnlyObservableCollection<RxAudioProcessingModeViewModel> RxAudioProcessingModes
        => audioSettings.RxAudioProcessingModes;
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioInputDevices
        => audioSettings.AudioInputDevices;
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioOutputDevices
        => audioSettings.AudioOutputDevices;
    public ReadOnlyObservableCollection<SubscriberCommandAuditEntry> SubscriberCommandAudit { get; }
    public ReadOnlyObservableCollection<DebugLogEntry> DebugLogEntries => debugLogs.Entries;
    public ReadOnlyObservableCollection<WebStreamViewModel> WebStreams { get; }
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry> CallHistory
        => historyRecording.CallHistory;
    public ReadOnlyObservableCollection<CallHistoryEntry> ActivityCallHistory
        => historyRecording.ActivityCallHistory;
    internal event NotifyCollectionChangedEventHandler? ActivityCallHistoryChanging
    {
        add => historyRecording.ActivityCallHistoryChanging += value;
        remove => historyRecording.ActivityCallHistoryChanging -= value;
    }
    public string ActivityZoneFilterButtonText => activityCurrentZoneOnly ? "Zone Wide" : "System Wide";
    public string ActivityReceiveFilterButtonText => activityReceiveEnabledOnly ? "Active" : "All";
    public IReadOnlyList<SubscriberCommandAuditEntry> ActivitySubscriberCommandAudit
        => SelectedSystem is null
            ? []
            : SubscriberCommandAudit
                .Where(entry => entry.SystemName.Equals(SelectedSystem.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    public ReadOnlyObservableCollection<CallHistoryEntry> FilteredCallHistory
        => historyRecording.FilteredCallHistory;
    internal event NotifyCollectionChangedEventHandler? FilteredCallHistoryChanging
    {
        add => historyRecording.FilteredCallHistoryChanging += value;
        remove => historyRecording.FilteredCallHistoryChanging -= value;
    }
    public bool HasAdvancedHistoryFilters => historyRecording.HasAdvancedHistoryFilters;
    public string HistoryFilterSummary => historyRecording.HistoryFilterSummary;
    public ReadOnlyObservableCollection<CallRecordingMetadata> Recordings
        => historyRecording.Recordings;
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleSelectedSystemOutputMuteCommand { get; }
    public ICommand ToggleSelectedZoneOutputMuteCommand { get; }
    public ICommand SendDtmfCommand { get; }
    public ICommand SendToneCommand { get; }
    public ICommand SaveDtmfPresetCommand { get; }
    public ICommand SaveTonePresetCommand { get; }
    public ICommand ApplyAudioInputSettingsCommand { get; }
    public ICommand ApplyRxAudioProcessingOptionsCommand { get; }
    public ICommand ApplyRecordingRetentionCommand { get; }
    public ICommand RefreshAudioDevicesCommand { get; }
    public ICommand ConnectionCommand => SelectedSystem?.IsConnected == true ? DisconnectCommand : ConnectCommand;
    public string ConnectionButtonText => networkDisabledDemo
        ? "Demo offline"
        : SelectedSystem?.IsConnected == true ? "Disconnect" : "Connect";
    public string ConnectionPillText => networkDisabledDemo
        ? "DEMO / OFFLINE"
        : SelectedSystem?.IsConnected == true ? "CONNECTED" : "OFFLINE";
    public string SelectedSystemName => SelectedSystem?.Name ?? "No system";
    public string SystemStatusText => SelectedSystem?.ConnectionStatus ?? "No configured system";
    public IReadOnlyList<string> DebugLogSeverityFilters => debugLogs.DebugLogSeverityFilters;
    public string DebugLogRetentionText => debugLogs.RetentionText;
    public IReadOnlyList<DebugLogEntry> FilteredDebugLogs => debugLogs.FilteredEntries;
    internal event NotifyCollectionChangedEventHandler? DebugLogCollectionChanging
    {
        add => debugLogs.CollectionChanging += value;
        remove => debugLogs.CollectionChanging -= value;
    }

    public string DebugLogFilterText
    {
        get => debugLogs.FilterText;
        set => debugLogs.FilterText = value;
    }

    public string DebugLogSeverityFilter
    {
        get => debugLogs.SeverityFilter;
        set => debugLogs.SeverityFilter = value;
    }

    public string CallHistoryFilterText
    {
        get => historyRecording.CallHistoryFilterText;
        set => historyRecording.CallHistoryFilterText = value;
    }

    public IReadOnlyList<string> RecordingDirectionFilters => historyRecording.RecordingDirectionFilters;
    public IReadOnlyList<string> RecordingProtocolFilters => historyRecording.RecordingProtocolFilters;
    public IReadOnlyList<string> RecordingEncryptionFilters => historyRecording.RecordingEncryptionFilters;

    public string RecordingDirectionFilter
    {
        get => historyRecording.RecordingDirectionFilter;
        set => historyRecording.RecordingDirectionFilter = value;
    }

    public string RecordingProtocolFilter
    {
        get => historyRecording.RecordingProtocolFilter;
        set => historyRecording.RecordingProtocolFilter = value;
    }

    public string RecordingEncryptionFilter
    {
        get => historyRecording.RecordingEncryptionFilter;
        set => historyRecording.RecordingEncryptionFilter = value;
    }

    public string RecordingSystemFilterText
    {
        get => historyRecording.RecordingSystemFilterText;
        set => historyRecording.RecordingSystemFilterText = value;
    }

    public string RecordingChannelFilterText
    {
        get => historyRecording.RecordingChannelFilterText;
        set => historyRecording.RecordingChannelFilterText = value;
    }

    public string RecordingTalkgroupFilterText
    {
        get => historyRecording.RecordingTalkgroupFilterText;
        set => historyRecording.RecordingTalkgroupFilterText = value;
    }

    public string RecordingSubscriberFilterText
    {
        get => historyRecording.RecordingSubscriberFilterText;
        set => historyRecording.RecordingSubscriberFilterText = value;
    }

    public string RecordingAliasFilterText
    {
        get => historyRecording.RecordingAliasFilterText;
        set => historyRecording.RecordingAliasFilterText = value;
    }

    public DateTimeOffset? RecordingStartDateFilter
    {
        get => historyRecording.RecordingStartDateFilter;
        set => historyRecording.RecordingStartDateFilter = value;
    }

    public DateTimeOffset? RecordingEndDateFilter
    {
        get => historyRecording.RecordingEndDateFilter;
        set => historyRecording.RecordingEndDateFilter = value;
    }

    public void ClearHistoryFilters()
        => historyRecording.ClearHistoryFilters();

    public bool ApplyRecordingRoot()
    {
        if (!callRecordings.TrySetRootPath(RecordingRootPathText, out string errorMessage))
        {
            RecordingRootPathText = callRecordings.RootPath;
            AudioStatusText = $"TAR storage unchanged: {errorMessage}";
            return false;
        }

        userSettings.RecordingRootPath = callRecordings.RootPath;
        PersistUserSettings();
        RefreshRecordings(pruneExpired: true);
        RecordingRootPathText = callRecordings.RootPath;
        AudioStatusText = $"TAR recordings now use {callRecordings.RootPath}.";
        return true;
    }

    public void ExportDebugLogs(string path)
    {
        int count = debugLogs.Export(path);
        ReportDebugLogExportSuccess(count, path);
    }

    internal void ExportDebugLogs(Stream destination, string destinationName)
    {
        int count = debugLogs.Export(destination);
        ReportDebugLogExportSuccess(count, destinationName);
    }

    internal void ReportDebugLogExportFailure(string message)
        => StatusText = $"Unable to export debug logs: {message}";

    private void ReportDebugLogExportSuccess(int count, string destinationName)
        => StatusText = $"Exported {count} redacted debug log " +
            $"entr{(count == 1 ? "y" : "ies")} to {destinationName}.";

    public IBrush ConnectionBrush => SelectedSystem?.IsConnected == true
        ? new SolidColorBrush(Color.Parse("#00C86A"))
        : new SolidColorBrush(Color.Parse("#7B8794"));

    public bool TrySendSubscriberCommand(
        SystemViewModel system,
        P25SubscriberCommand command,
        string? destinationText,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (!P25SubscriberCommandCodec.TryParseSubscriberId(destinationText, out uint destinationId))
        {
            message = "Enter a P25 subscriber RID from 1 to 16777215.";
            RecordSubscriberCommandAudit(system.Name, command, 0, false, message);
            StatusText = message;
            return false;
        }

        if (!system.IsConnected)
        {
            message = $"{system.Name} is not connected to an FNE.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = message;
            return false;
        }

        if (system.SourceId is not uint sourceId || !P25SubscriberCommandCodec.IsValidSubscriberId(sourceId))
        {
            message = $"{system.Name} does not have a configured source RID.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = message;
            return false;
        }

        try
        {
            system.SendP25SubscriberCommand(command, destinationId);
            message = "Sent; acknowledgement decoding is pending.";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, true, message);
            StatusText = $"{system.Name}: {CommandName(command)} to RID {destinationId} sent.";
            return true;
        }
        catch (Exception exception)
        {
            message = $"Unable to send command: {exception.Message}";
            RecordSubscriberCommandAudit(system.Name, command, destinationId, false, message);
            StatusText = $"{system.Name}: {message}";
            return false;
        }
    }

    public bool RetainPatchStateOnStartup
    {
        get => userSettings.RetainPatchStateOnStartup;
        set
        {
            if (userSettings.RetainPatchStateOnStartup == value)
                return;
            userSettings.RetainPatchStateOnStartup = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RetainPatchStateOnStartup)));
        }
    }

    public void OpenRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.IsPlayable ||
            !callRecordings.TryGetRecordingPath(metadata, out string recordingPath))
        {
            AudioStatusText = "The selected recording file is no longer available.";
            RefreshRecordings();
            return;
        }

        try
        {
            Process.Start(CreateRevealRecordingStartInfo(
                recordingPath,
                OperatingSystem.IsWindows(),
                OperatingSystem.IsMacOS()));
            AudioStatusText = $"Opened recording location: {metadata.FileName}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            AudioStatusText = $"Unable to show recording in its folder: {exception.Message}";
        }
    }

    internal static ProcessStartInfo CreateRevealRecordingStartInfo(
        string recordingPath,
        bool isWindows,
        bool isMacOS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        string fullPath = Path.GetFullPath(recordingPath);

        if (isWindows)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(fullPath);
            return startInfo;
        }

        if (isMacOS)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(fullPath);
            return startInfo;
        }

        return new ProcessStartInfo
        {
            FileName = Path.GetDirectoryName(fullPath) ?? fullPath,
            UseShellExecute = true
        };
    }

    public async Task PlayRecordingAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.IsPlayable ||
            !callRecordings.TryGetRecordingPath(metadata, out string recordingPath))
        {
            AudioStatusText = "The selected recording file is no longer available.";
            RefreshRecordings();
            return;
        }

        try
        {
            await recordingPlayback.StartAsync(recordingPath).ConfigureAwait(false);
            AudioStatusText = $"Playing recording: {metadata.FileName}";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            AudioStatusText = $"Unable to play recording: {exception.Message}";
        }
    }

    public async Task PlayCallHistoryRecordingAsync(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Recording is not CallRecordingMetadata metadata)
        {
            AudioStatusText = "No TAR recording is available for this event.";
            return;
        }

        await PlayRecordingAsync(metadata).ConfigureAwait(false);
    }

    public async Task ToggleCallHistoryRecordingPlaybackAsync(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsRecordingPlaying)
            await StopRecordingPlaybackAsync().ConfigureAwait(false);
        else
            await PlayCallHistoryRecordingAsync(entry).ConfigureAwait(false);
    }

    public async Task StopRecordingPlaybackAsync()
    {
        await recordingPlayback.StopAsync().ConfigureAwait(false);
        AudioStatusText = "Recording playback stopped.";
    }

    public async Task DeleteRecordingAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        try
        {
            if (callRecordings.TryGetRecordingPath(metadata, out string recordingPath))
            {
                // Match and stop under the playback coordinator's gate. The
                // stop must finish before the recording file is removed.
                await recordingPlayback.StopIfPlayingAsync(recordingPath).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
                AudioStatusText = $"Unable to stop recording playback for deletion: {exception.Message}").ConfigureAwait(false);
            return;
        }

        if (!callRecordings.DeleteRecording(metadata))
        {
            await RunOnUiThreadAsync(() =>
            {
                AudioStatusText = "The selected recording could not be deleted.";
                RefreshRecordings();
            }).ConfigureAwait(false);
            return;
        }

        // Active playback shutdown resumes on a pool thread. Marshal every
        // observable History/catalog mutation back to Avalonia's UI thread.
        await RunOnUiThreadAsync(() =>
        {
            AudioStatusText = $"Deleted recording: {metadata.FileName}";
            RecordRecordingCatalogMutation();
            recordingEntries.Remove(metadata);
            callHistory.RemoveRecording(metadata);
            NotifyCallHistoryChanged();
        }).ConfigureAwait(false);
    }

    public void SetRecordingIgnoredSubscribers(ChannelViewModel channel, IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(subscriberIds);
        List<uint> normalized = subscriberIds
            .Where(subscriberId => subscriberId != 0)
            .Distinct()
            .OrderBy(subscriberId => subscriberId)
            .ToList();
        userSettings.RecordingIgnoredSubscriberIds[channel.SettingsKey] = normalized;
        channel.SetIgnoredSubscriberIds(normalized);
        PersistUserSettings();
    }

    public bool TrySaveRecordingIgnoredSubscribers(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        List<uint> subscriberIds = [];
        foreach (string token in channel.IgnoredSubscriberIdsText.Split(
                     [',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(token, out uint subscriberId) || subscriberId == 0)
            {
                AudioStatusText = $"Ignored subscriber IDs must be positive integers: '{token}'.";
                return false;
            }

            subscriberIds.Add(subscriberId);
        }

        SetRecordingIgnoredSubscribers(channel, subscriberIds);
        AudioStatusText = subscriberIds.Count == 0
            ? $"Recording ignores cleared for {channel.Name}."
            : $"Recording ignores {subscriberIds.Distinct().Count()} subscriber ID(s) on {channel.Name}.";
        return true;
    }

    public ChannelViewModel? SelectedChannel => selectedChannel;
    public bool HasSelectedZone => SelectedSystem?.SelectedZone is not null;

    public SystemViewModel? SelectedSystem
    {
        get => selectedSystem;
        set
        {
            if (ReferenceEquals(selectedSystem, value))
                return;

            selectedSystem = value;
            foreach (SystemViewModel system in Systems)
                system.SetSelected(ReferenceEquals(system, selectedSystem));
            userSettings.LastSelectedSystemName = value?.Name;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedZone)));
            RefreshActivityCallHistory();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySubscriberCommandAudit)));
            NotifyConnectionPresentationChanged();
            NotifySelectedOutputMutePresentationChanged();
            (ToggleSelectedSystemOutputMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleSelectedZoneOutputMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            RaiseGeneratedAudioCanExecuteChanged();
        }
    }

    public void ToggleActivityZoneFilter()
    {
        activityCurrentZoneOnly = !activityCurrentZoneOnly;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityZoneFilterButtonText)));
        RefreshActivityCallHistory();
    }

    public void ToggleActivityReceiveFilter()
    {
        activityReceiveEnabledOnly = !activityReceiveEnabledOnly;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityReceiveFilterButtonText)));
        RefreshActivityCallHistory();
    }

    private void HandleActivityChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelViewModel.IsAudioEnabled) ||
            sender is not ChannelViewModel channel ||
            SelectedSystem?.Channels.Contains(channel) != true)
        {
            return;
        }

        if (uiDispatcher.CheckAccess())
            RefreshActivityCallHistory();
        else
            uiDispatcher.Post(RefreshActivityCallHistory);
    }

    private void HandleSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemViewModel.SelectedZone) && ReferenceEquals(sender, SelectedSystem))
        {
            RefreshActivityCallHistory();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedZone)));
            NotifySelectedOutputMutePresentationChanged();
            (ToggleSelectedZoneOutputMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public async ValueTask StartKeyboardPttAsync(CancellationToken cancellationToken = default)
    {
        if (networkDisabledDemo)
        {
            TransmitStatusText = "Demo TX · local pointer capture · network output disabled.";
            return;
        }

        bool firstStart = !pttSession.IsStarted;
        PttSessionStartResult result =
            await pttSession.StartAsync(cancellationToken).ConfigureAwait(false);
        if (firstStart)
        {
            TransmitStatusText = DescribeKeyboardPttReadiness(
                result.GlobalKeyboard,
                result.ActiveSystemKeyboard);
        }
        if (!pttSession.HasSerialSource)
            return;
        if (result.SerialError is not null)
        {
            SerialPttStatusText = $"Serial PTT unavailable on {SerialPttPortName}: {result.SerialError.Message}";
            TransmitStatusText = $"PTT idle; serial source unavailable: {result.SerialError.Message}";
            return;
        }
        SerialPttStatusText = $"Serial PTT ready on {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
        TransmitStatusText = $"PTT idle; serial source {SerialPttPortName} ready for {SerialPttScopeText}.";
    }

    public void SelectChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (AnyPttSourcePressed && selectedChannel is not null && !ReferenceEquals(selectedChannel, channel))
            return;
        if (ReferenceEquals(selectedChannel, channel))
            return;

        selectedChannel = channel;
        selectedSystem = Systems.FirstOrDefault(system => system.Channels.Contains(channel)) ?? selectedSystem;
        userSettings.LastSelectedSystemName = selectedSystem?.Name;
        userSettings.LastSelectedChannelKey = channel.SettingsKey;
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChannel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
        RaiseGeneratedAudioCanExecuteChanged();
    }

    public void ToggleChannelTransmitSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for TX.";
            return;
        }

        channel.SetTransmitSelected(!channel.IsTransmitSelected);
        userSettings.TransmitSelectedChannelKeys = Systems
            .SelectMany(system => system.Channels)
            .Where(candidate => candidate.IsTransmitSelected)
            .Select(candidate => candidate.SettingsKey)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = channel.IsTransmitSelected
            ? $"{channel.Name} selected for global TX."
            : $"{channel.Name} removed from global TX.";
    }

    public void ToggleAllTransmitSelection()
    {
        ChannelViewModel[] candidates = (SelectedSystem?.Channels ?? Systems.SelectMany(system => system.Channels))
            .Where(channel => channel.CanTransmit)
            .ToArray();
        if (candidates.Length == 0)
        {
            TransmitStatusText = "No transmit-capable channels are available in the selected system.";
            return;
        }

        bool select = candidates.Any(channel => !channel.IsTransmitSelected);
        foreach (ChannelViewModel channel in candidates)
            channel.SetTransmitSelected(select);

        userSettings.TransmitSelectedChannelKeys = Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsTransmitSelected)
            .Select(channel => channel.SettingsKey)
            .ToList();
        PersistUserSettings();
        TransmitStatusText = select
            ? $"Selected {candidates.Length} transmit-capable channel(s) for global TX."
            : "Cleared global TX selection.";
    }

    public void ToggleChannelPageSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for paging.";
            return;
        }

        channel.SetPageSelected(!channel.IsPageSelected);
        TransmitStatusText = channel.IsPageSelected
            ? $"{channel.Name} armed for QCII paging."
            : $"{channel.Name} removed from QCII paging.";
    }

    public void ToggleChannelAlertSelection(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"{channel.Name} cannot be selected for alerts.";
            return;
        }

        channel.SetAlertSelected(!channel.IsAlertSelected);
        RaiseGeneratedAudioCanExecuteChanged();
        TransmitStatusText = channel.IsAlertSelected
            ? $"{channel.Name} armed for DTMF and alert tones."
            : $"{channel.Name} removed from alert-tone targeting.";
    }

    public async Task SetGlobalPttKeyAsync(KeyboardPttKey key)
    {
        SelectedGlobalPttKey = key;
        if (key != KeyboardPttKey.None && key == pttSession.ActiveSystemKey)
        {
            SelectedGlobalPttKey = pttSession.GlobalKey;
            TransmitStatusText = $"{key} is already assigned to active-system PTT.";
            return;
        }
        if (pttSession.GlobalKey == key)
            return;

        await ReplaceKeyboardPttBindingAsync(
            PttTargetScope.AllSelectedResources,
            key).ConfigureAwait(false);
        userSettings.GlobalPttKey = key.ToString();
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalPttKeyText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        TransmitStatusText = key == KeyboardPttKey.None
            ? "Keyboard global PTT disabled."
            : $"Global PTT key set to {key}.";
    }

    public async Task SetActiveSystemPttKeyAsync(KeyboardPttKey key)
    {
        SelectedActiveSystemPttKey = key;
        if (key != KeyboardPttKey.None && key == pttSession.GlobalKey)
        {
            SelectedActiveSystemPttKey = pttSession.ActiveSystemKey;
            TransmitStatusText = $"{key} is already assigned to global PTT.";
            return;
        }
        if (pttSession.ActiveSystemKey == key)
            return;

        await ReplaceKeyboardPttBindingAsync(
            PttTargetScope.ActiveSystem,
            key).ConfigureAwait(false);
        userSettings.ActiveSystemPttKey = key.ToString();
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveSystemPttKeyText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        TransmitStatusText = key == KeyboardPttKey.None
            ? "Active-system keyboard PTT disabled."
            : $"Active-system PTT key set to {key}.";
    }

    private async Task ReplaceKeyboardPttBindingAsync(
        PttTargetScope scope,
        KeyboardPttKey key)
    {
        await pttSession.ReplaceKeyboardBindingAsync(
            scope,
            key,
            async () =>
            {
                // Detaching a pressed binding suppresses its release event.
                // Stop active TX first so rebinding cannot leave PTT latched.
                ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
                if (active.Length > 0)
                    await StopTransmitAsync(active).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    public async Task ToggleChannelReceiveAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        await ChangeChannelReceiveSelectionAsync(channel, enabled: null).ConfigureAwait(false);
    }

    public async Task DisableAllReceiveAsync()
        => await SetReceiveAsync(ReceiveSelectionScope.All, enabled: false).ConfigureAwait(false);

    public async Task EnableAllReceiveAsync()
        => await SetReceiveAsync(ReceiveSelectionScope.All, enabled: true).ConfigureAwait(false);

    public async Task EnableSelectedZoneReceiveAsync()
        => await SetReceiveAsync(ReceiveSelectionScope.SelectedZone, enabled: true).ConfigureAwait(false);

    public async Task DisableSelectedZoneReceiveAsync()
        => await SetReceiveAsync(ReceiveSelectionScope.SelectedZone, enabled: false).ConfigureAwait(false);

    internal IReadOnlyList<ChannelViewModel> GetReceiveScopeChannels(ReceiveSelectionScope scope)
        => scope switch
        {
            ReceiveSelectionScope.All => Systems
                .SelectMany(system => system.Channels)
                .Distinct()
                .ToArray(),
            ReceiveSelectionScope.SelectedZone => SelectedSystem?.SelectedZone?.Channels
                .Distinct()
                .ToArray() ?? [],
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };

    private async Task SetReceiveAsync(ReceiveSelectionScope scope, bool enabled)
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (ChannelViewModel channel in GetReceiveScopeChannels(scope))
            {
                try
                {
                    await ApplyChannelReceiveSelectionAsync(channel, enabled).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await ReportReceiveSelectionFailureAsync(channel, exception).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private async Task ChangeChannelReceiveSelectionAsync(
        ChannelViewModel channel,
        bool? enabled)
    {
        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool targetEnabled = enabled ?? !channel.IsAudioEnabled;
            await ApplyChannelReceiveSelectionAsync(channel, targetEnabled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ReportReceiveSelectionFailureAsync(channel, exception).ConfigureAwait(false);
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private async Task ApplyChannelReceiveSelectionAsync(
        ChannelViewModel channel,
        bool enabled)
    {
        if (enabled)
        {
            bool liveSessionMissing = !receiveOutputMutePolicy.IsMuted(channel) &&
                !audioCoordinator.LivePlaybackChannels.Contains(channel);
            if (!channel.IsAudioEnabled ||
                !audioCoordinator.IsActive(channel) ||
                (!channel.IsAudioSuspended && liveSessionMissing))
            {
                await StartAudioAsync(channel, persistSelection: true).ConfigureAwait(false);
            }
            return;
        }

        if (channel.IsAudioEnabled)
            await StopAudioAsync(channel, persistSelection: true).ConfigureAwait(false);
    }

    private async Task ReportReceiveSelectionFailureAsync(
        ChannelViewModel channel,
        Exception exception)
    {
        await RunOnUiThreadAsync(() =>
        {
            AudioStatusText = $"Unable to change RX selection for {channel.Name}: {exception.Message}";
            AddDebugLog(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Warning,
                $"RX selection change failed on {channel.Name}: {exception}");
        }).ConfigureAwait(false);
    }

    public async Task<bool> StartChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        if (channel.IsTransmitting)
            return true;
        if (!channel.IsPttControlEnabled)
        {
            TransmitStatusText = channel.IsReceivePresentationActive
                ? $"PTT unavailable: {channel.Name} is currently receiving."
                : $"PTT unavailable for {channel.Name}: the channel is RX-only or its encryption key is unavailable.";
            return false;
        }
        ObservePttActivationSource(PttActivationSource.LocalChannelControl);
        await StartTransmitAsync(channel).ConfigureAwait(false);
        return channel.IsTransmitting;
    }

    public async Task StopChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.IsTransmitting)
            return;
        await StopTransmitAsync(channel).ConfigureAwait(false);
    }

    public bool HandleKeyboardPttDown(KeyboardPttKey key)
        => pttSession.HandleKeyDown(key);

    public bool HandleKeyboardPttUp(KeyboardPttKey key)
        => pttSession.HandleKeyUp(key);

    public bool IsConfiguredPttKey(KeyboardPttKey key)
        => pttSession.IsConfiguredKey(key);

    internal IReadOnlyList<ChannelViewModel> GetSelectedTransmitTargets(PttTargetScope scope)
    {
        IEnumerable<SystemViewModel> systems = scope == PttTargetScope.ActiveSystem
            ? SelectedSystem is null
                ? []
                : [SelectedSystem]
            : Systems;
        return systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.IsTransmitSelected)
            .ToArray();
    }

    internal IReadOnlyList<ChannelViewModel> GetSerialPttTargets()
        => GetSelectedTransmitTargets(GetSerialPttTargetScope());

    public static MainWindowViewModel Load(string? configurationPath)
    {
        DesktopRuntimeDependencies dependencies = DesktopRuntimeDependencies.CreateDefault();
        return new ConsoleSessionFactory(dependencies).Create(
            new ConsoleSessionLoader(dependencies.UserSettingsStore).Load(configurationPath));
    }

    internal static MainWindowViewModel Load(
        string? configurationPath,
        UserSettingsStore userSettingsStore,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null,
        IUiDispatcher? uiDispatcher = null,
        bool networkDisabledDemo = false)
    {
        ArgumentNullException.ThrowIfNull(userSettingsStore);
        var dependencies = new DesktopRuntimeDependencies(
            userSettingsStore,
            serialPortProvider ?? SerialPttSource.GetAvailablePortNames,
            serialPttFactory ?? ((portName, baudRate) => new SerialPttSource(portName, baudRate)),
            uiDispatcher ?? AvaloniaUiDispatcher.Instance,
            networkDisabledDemo);
        return new ConsoleSessionFactory(dependencies).Create(
            new ConsoleSessionLoader(userSettingsStore).Load(configurationPath));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposeStarted, 1);
        return sessionRuntime.DisposeAsync();
    }

    public Task ToggleSystemConnectionAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!networkDisabledDemo)
            return connectionSession.ToggleAsync(system);

        StatusText = "NEO deterministic demo · network connection requests are disabled.";
        TransmitStatusText = "Demo safety boundary: no FNE connection or outbound traffic was attempted.";
        return Task.CompletedTask;
    }

    private void HandleSystemStatus(SystemViewModel system, FneConnectionStatus status)
    {
        void Apply()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            system.ApplyStatus(status);
            StatusText = $"{system.Name}: {status.State} — {status.Message}";
            NotifyConnectionPresentationChanged();
            if (status.State == FneConnectionState.Connected)
            {
                ScheduleConfiguredP25Keys(system);
                TaskObservation.Observe(ReconcileReceiveSessionsAsync());
            }
            else
            {
                p25KeyRequestCoordinator.Cancel(system.Name);
            }
            bool stateChanged = !lastConnectionStates.TryGetValue(system.Name, out FneConnectionState previousState) ||
                previousState != status.State;
            lastConnectionStates[system.Name] = status.State;
            if (stateChanged &&
                previousState == FneConnectionState.Connected &&
                status.State != FneConnectionState.Connected)
            {
                adaptiveReceiveJitter.Reset(system.Name);
                receiveJitterEffectiveness.Reset(system.Name);
            }
            if (stateChanged &&
                previousState == FneConnectionState.Connected &&
                status.State != FneConnectionState.Connected &&
                p25KeyRing is not null)
            {
                p25KeyRing.ClearFneKeys(system.Name);
                RefreshP25KeyState();
                TaskObservation.Observe(SyncPatchSourceDecodeAsync());
            }
            if (stateChanged && status.State is FneConnectionState.Connected or FneConnectionState.Disconnected or FneConnectionState.Faulted)
            {
                string stateText = status.State.ToString().ToLowerInvariant();
                AddEventHistory(
                    "FNE",
                    $"{system.Name} {stateText}",
                    system.SourceId?.ToString(CultureInfo.InvariantCulture),
                    system.Endpoint);
            }
            bool shouldPlayChime = connectionChimeTracker.ShouldPlay(system.Name, status.State);
            if (stateChanged && shouldPlayChime)
                TaskObservation.Observe(PlayConnectionChimeAsync(system.Name, status.State));
            RaiseGeneratedAudioCanExecuteChanged();
        }

        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        if (uiDispatcher.CheckAccess())
            Apply();
        else
            uiDispatcher.Post(Apply);
    }

    private async Task PlayConnectionChimeAsync(string systemName, FneConnectionState state)
    {
        if (!ConnectionChimes)
            return;

        try
        {
            LocalTonePlaybackRequest cue = state == FneConnectionState.Connected
                ? LocalToneCues.ConnectionEstablished
                : LocalToneCues.ConnectionLost;
            await localTonePlayer.PlayAsync(cue).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            uiDispatcher.Post(() =>
                AudioStatusText = $"{systemName} connection chime unavailable: {exception.Message}");
        }
    }

    private void HandleSystemLog(object? sender, FneLogEntry entry)
        => AddDebugLog(entry.Timestamp, entry.SystemName, entry.Severity, entry.Message);

    private void HandlePatchForwardingDiagnostic(PatchForwardingDiagnostic diagnostic)
        => AddDebugLog(
            diagnostic.ObservedAt,
            "PATCH",
            diagnostic.IsFailure ? DebugLogSeverity.Warning : DebugLogSeverity.Debug,
            diagnostic.Message);

    private void AddDebugLog(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
        => debugLogs.Add(timestamp, source, severity, message);

    private void HandleDebugLogWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string propertyName = e.PropertyName switch
        {
            nameof(DebugLogWorkspace.FilterText) => nameof(DebugLogFilterText),
            nameof(DebugLogWorkspace.SeverityFilter) => nameof(DebugLogSeverityFilter),
            nameof(DebugLogWorkspace.RetentionText) => nameof(DebugLogRetentionText),
            _ => e.PropertyName ?? string.Empty
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ScheduleConfiguredP25Keys(SystemViewModel system)
    {
        // Request every configured key even when a local fallback is available.
        // Match the legacy console's post-connect settling delay and per-request
        // pacing so an FNE/KMM has time to service every configured key.
        if (p25KeyRing is null || Volatile.Read(ref disposeStarted) != 0)
            return;

        _ = p25KeyRequestCoordinator.Schedule(
            system.Name,
            ResolveConfiguredP25KeyRequests(system.Channels),
            () => system.IsConnected,
            system.RequestP25Key,
            exception =>
            {
                if (Volatile.Read(ref disposeStarted) != 0)
                    return;
                uiDispatcher.Post(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                        StatusText = $"{system.Name}: P25 key request unavailable — {exception.Message}";
                });
            });
    }

    private void HandleSystemJitterBufferChanged(object? sender, EventArgs e)
    {
        if (sender is SystemViewModel system)
            TaskObservation.Observe(ApplyRxJitterBufferAsync(system));
    }

    internal static IReadOnlyList<(byte AlgorithmId, ushort KeyId)> ResolveConfiguredP25KeyRequests(
        IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels
            .Where(channel => channel.Definition.Protocol == ChannelProtocol.P25 && channel.Definition.IsEncrypted)
            .Select(channel =>
            {
                byte algorithmId = 0;
                ushort keyId = 0;
                bool valid = P25KeyRing.TryParseAlgorithmId(
                        channel.Definition.EncryptionAlgorithm,
                        out algorithmId) &&
                    P25KeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out keyId);
                return (Valid: valid, AlgorithmId: algorithmId, KeyId: keyId);
            })
            .Where(request => request.Valid)
            .Select(request => (request.AlgorithmId, request.KeyId))
            .Distinct()
            .ToArray();
    }

    private void HandleSystemKeyResponse(object? sender, FneKeyResponse response)
    {
        if (sender is not SystemViewModel system ||
            p25KeyRing is null ||
            !response.SystemName.Equals(system.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        void Apply()
        {
            try
            {
                p25KeyRing.AddOrReplaceFromFne(
                    system.Name,
                    response.AlgorithmId,
                    response.KeyId,
                    response.KeyMaterial.Span);
                RefreshP25KeyState();
                StatusText = $"{system.Name}: P25 key 0x{response.KeyId:X4} received through FNE/KMM.";
                TaskObservation.Observe(SyncPatchSourceDecodeAsync());
            }
            catch (ArgumentException exception)
            {
                StatusText = $"{system.Name}: rejected P25 KMM key 0x{response.KeyId:X4} — {exception.Message}";
            }
        }

        if (uiDispatcher.CheckAccess())
            Apply();
        else
            uiDispatcher.Post(Apply);
    }

    private void RefreshP25KeyState()
    {
        foreach (ChannelViewModel channel in Systems.SelectMany(candidate => candidate.Channels))
            channel.RefreshEncryptionState();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyStatusItems)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoKeyStatusItems)));
    }

    private void HandleChannelEncryptionChanged(object? sender, bool encrypted)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.TransmitEncryptionStates[channel.SettingsKey] = encrypted;
        PersistUserSettings();
    }

    private void HandleChannelRecordingChanged(object? sender, bool enabled)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.RecordingEnabledChannelKeys.RemoveAll(
            key => key.Equals(channel.SettingsKey, StringComparison.OrdinalIgnoreCase));
        if (enabled)
            userSettings.RecordingEnabledChannelKeys.Add(channel.SettingsKey);
        PersistUserSettings();

        if (!enabled)
        {
            callRecordings.StopChannel(channel);
            TaskObservation.Observe(StopRecordingDecodeIfUnusedAsync(channel));
            return;
        }

        // Accept and retain inbound frames immediately. The ordered worker
        // will wait for EnsureRecordingAudioAsync before decoding them.
        receiveAudioWork.Start(channel);
        TaskObservation.Observe(EnsureRecordingAudioAsync(channel));
    }

    private void HandleChannelVolumeChanged(object? sender, double volume)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.ChannelVolumes[channel.SettingsKey] = volume;
        PersistUserSettings();
        TaskObservation.Observe(audioCoordinator.SetGainAsync(channel, volume));
    }

    private void HandleChannelStereoBalanceChanged(object? sender, double balance)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.ChannelStereoBalances[channel.SettingsKey] = balance;
        PersistUserSettings();
        TaskObservation.Observe(audioCoordinator.SetBalanceAsync(channel, balance));
    }

    private async Task StartWebStreamAsync(WebStreamViewModel stream)
    {
        if (networkDisabledDemo)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, false, "Demo offline");
                AudioStatusText = "Demo safety boundary: web-stream network access is disabled.";
            });
            return;
        }

        try
        {
            await webStreamPlayback.StartAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            await uiDispatcher.InvokeAsync(() =>
                AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}");
        }
        catch (OperationCanceledException)
        {
            await uiDispatcher.InvokeAsync(() =>
                stream.SetPlaybackState(false, false, false, false, "Off"));
        }
        catch (Exception exception)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, true, $"Failed: {exception.Message}");
                AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
            });
        }
    }

    private async Task StopWebStreamAsync(WebStreamViewModel stream)
    {
        try
        {
            await webStreamPlayback.StopAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            await uiDispatcher.InvokeAsync(() =>
                AudioStatusText = $"Web stream {stream.Name}: Off");
        }
        catch (OperationCanceledException)
        {
            await uiDispatcher.InvokeAsync(() =>
                stream.SetPlaybackState(false, false, false, false, "Off"));
        }
        catch (Exception exception)
        {
            await uiDispatcher.InvokeAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, true, $"Failed to stop: {exception.Message}");
                AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
            });
        }
    }

    private void HandleWebStreamVolumeChanged(object? sender, double volume)
    {
        if (sender is not WebStreamViewModel stream)
            return;

        userSettings.WebStreamVolumes[stream.Name] = volume;
        webStreamPlayback.SetVolume(stream, volume);
        PersistUserSettings();
    }

    private void HandleWebStreamPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WebStreamViewModel.IsActive) && sender is WebStreamViewModel stream)
            PersistSelectedWebStreamState(stream);
    }

    private async Task RestoreSelectedWebStreamsAsync()
    {
        if (!userSettings.RestoreSelectedChannelsOnStartup || userSettings.SelectedWebStreams.Count == 0)
            return;

        foreach (WebStreamViewModel stream in webStreams.Where(stream =>
            WebStreamSelectionIdentity.IsAuthorized(
                userSettings.SelectedWebStreams,
                loadedCodeplugPath,
                stream)))
        {
            await StartWebStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private void PersistSelectedWebStreamState(WebStreamViewModel stream)
    {
        if (!userSettings.RestoreSelectedChannelsOnStartup)
            return;

        HashSet<string> selectedIdentities = userSettings.SelectedWebStreams
            .Where(WebStreamSelectionIdentity.IsVersioned)
            .ToHashSet(StringComparer.Ordinal);
        string identity = WebStreamSelectionIdentity.Create(loadedCodeplugPath, stream);
        if (stream.IsActive && !stream.IsFailed)
        {
            if (identity.Length > 0)
                selectedIdentities.Add(identity);
        }
        else
        {
            selectedIdentities.Remove(identity);
        }
        userSettings.SelectedWebStreams = selectedIdentities.ToList();
        PersistUserSettings();
    }

    public bool SaveWebStreamOutputDevice(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string deviceId = stream.OutputDeviceIdText.Trim();
        if (deviceId.Length > 256)
        {
            AudioStatusText = "Output device IDs must be 256 characters or fewer.";
            return false;
        }

        if (deviceId.Length == 0 || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            userSettings.WebStreamOutputDeviceIds.Remove(stream.Name);
        else
            userSettings.WebStreamOutputDeviceIds[stream.Name] = deviceId;

        PersistUserSettings();
        stream.RestoreOutputDeviceId(deviceId);
        AudioStatusText = stream.IsActive
            ? $"Output route saved for {stream.Name}; stop and start it again to apply the route."
            : $"Output route saved for {stream.Name}.";
        return true;
    }

    public bool SaveChannelOutputDevice(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        string deviceId = channel.OutputDeviceIdText.Trim();
        if (deviceId.Length > 256)
        {
            AudioStatusText = "Output device IDs must be 256 characters or fewer.";
            return false;
        }

        if (deviceId.Length == 0 || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            userSettings.ChannelOutputDeviceIds.Remove(channel.SettingsKey);
        else
            userSettings.ChannelOutputDeviceIds[channel.SettingsKey] = deviceId;

        PersistUserSettings();
        channel.RestoreOutputDeviceId(deviceId);
        AudioStatusText = channel.IsAudioEnabled
            ? $"Output route saved for {channel.Name}; stop and listen again to apply it."
            : $"Output route saved for {channel.Name}.";
        return true;
    }

    private double GetChannelVolume(ChannelViewModel channel)
    {
        return userSettings.ChannelVolumes.TryGetValue(channel.SettingsKey, out double volume)
            ? volume
            : 1.0;
    }

    private double GetChannelStereoBalance(ChannelViewModel channel)
    {
        return userSettings.ChannelStereoBalances.TryGetValue(channel.SettingsKey, out double balance)
            ? balance
            : 0.0;
    }

    // Apple Voice Processing I/O is a full-duplex route. The application-level
    // provider mixes receive audio, local cues, web streams, and recordings into
    // its one output stream while keeping vocoder processing independent.
    private IAudioBackend CreateReceiveAudioBackend()
        => audioBackendProvider.CreateBackend();

    private IVocoderBackend CreateReceiveVocoderBackend()
        => new SoftwareVocoderBackend(Volatile.Read(ref receiveAudioProcessingOptions));

    // ProcessedAudioCapture confines DVM Console gain/EQ/AGC to microphone
    // samples. The provider coordinates the platform's physical I/O route.
    private IAudioBackend CreateTransmitAudioBackend()
        => audioBackendProvider.CreateBackend();

    private IAudioBackend CreateNativeAudioBackend(ApplicationAudioConfiguration configuration)
        => AudioBackendFactory.CreateDefault(
            Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"),
            configuration.ProcessingMode,
            configuration.InputDeviceId,
            configuration.OutputDeviceId,
            configuration.HighQualityBluetoothAudio);

    private ApplicationAudioConfiguration CreateApplicationAudioConfiguration()
        => new(
            GetConfiguredAudioProcessingMode(),
            userSettings.AudioInputDeviceId,
            userSettings.AudioOutputDeviceId,
            userSettings.HighQualityBluetoothAudioEnabled);

    private void HandleWarmMicrophoneReconciled(object? sender, LatestBooleanStateResult result)
    {
        uiDispatcher.Post(() =>
        {
            if (result.Error is not null)
            {
                AudioStatusText = $"Unable to change warm microphone state: {result.Error.Message}";
            }
            else if (result.Desired)
            {
                AudioStatusText = "Transmit microphone is warm. This is generally useful only for Bluetooth headsets to reduce PTT latency and may lower output audio quality.";
            }
            else
            {
                AudioStatusText = transmitCoordinator.ActiveChannels.Count > 0
                    ? "Warm microphone mode disabled; the active transmission continues."
                    : "Warm microphone mode disabled; the microphone will open on PTT.";
            }
        });
    }

    private AudioProcessingMode GetConfiguredAudioProcessingMode()
        => userSettings.AudioProcessingMode switch
        {
            UserSettings.WindowsCommunicationsProcessingMode when OperatingSystem.IsWindows() =>
                AudioProcessingMode.WindowsCommunications,
            _ => AudioProcessingMode.DvmConsole
        };

    private AudioProcessingMode GetSelectedAudioProcessingMode()
        => SelectedAudioProcessingMode switch
        {
            WindowsCommunicationsProcessingDisplay => AudioProcessingMode.WindowsCommunications,
            _ => AudioProcessingMode.DvmConsole
        };

    private static string ToAudioProcessingModeDisplay(string? mode)
        => mode switch
        {
            UserSettings.WindowsCommunicationsProcessingMode when OperatingSystem.IsWindows() =>
                WindowsCommunicationsProcessingDisplay,
            _ => DvmConsoleProcessingDisplay
        };

    private static bool NormalizeHiddenAudioProcessingMode(UserSettings settings)
    {
        if (!string.Equals(
                settings.AudioProcessingMode,
                UserSettings.AppleVoiceProcessingMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        settings.AudioProcessingMode = UserSettings.DvmConsoleAudioProcessingMode;
        return true;
    }

    private string? GetChannelOutputDeviceId(ChannelViewModel channel)
    {
        if (userSettings.ChannelOutputDeviceIds.TryGetValue(channel.SettingsKey, out string? channelDeviceId))
            return channelDeviceId;
        return userSettings.AudioOutputDeviceId;
    }

    private string? GetWebStreamOutputDeviceId(WebStreamViewModel stream)
    {
        if (userSettings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? streamDeviceId))
            return streamDeviceId;
        return userSettings.AudioOutputDeviceId;
    }

    private async Task EnsureRecordingAudioAsync(ChannelViewModel channel)
    {
        if (audioCoordinator.IsActive(channel))
            return;

        try
        {
            await audioCoordinator.EnsureDecodeAsync(
                channel,
                livePlaybackEnabledWhenCreated: channel.IsAudioEnabled).ConfigureAwait(false);
            receiveAudioWork.Start(channel);
            receivePipelineTimingReporter.Reset(channel);
            receiveJitterEventReporter.Reset(channel);
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
                AudioStatusText = $"TAR decode unavailable for {channel.Name}: {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    private async Task StopRecordingDecodeIfUnusedAsync(ChannelViewModel channel)
    {
        if (channel.IsAudioEnabled || !audioCoordinator.IsActive(channel))
            return;

        try
        {
            await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
            receiveJitterEventReporter.Reset(channel);
            await audioCoordinator.StopAsync(channel).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposeStarted) != 0)
        {
            // Application shutdown already owns receive-session cleanup.
        }
        catch (Exception exception)
        {
            AddDebugLog(
                DateTimeOffset.UtcNow,
                "RX",
                DebugLogSeverity.Warning,
                $"TAR decoder cleanup failed for {channel.Name}: {exception.Message}");
        }
    }

    private void HandleDecodedSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        // An enabled patch owns a dedicated jitter-buffered decoder for its
        // source. Do not feed the same PCM a second time from Listen or TAR.
        if (!patchSourceDecode.IsActive(channel))
            patchForwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples);
        ChannelViewModel? recordingTarget = ResolveReceiveRecordingTarget(channel);
        if (recordingTarget is not null)
        {
            callRecordings.WriteEpisodeSamples(
                recordingTarget,
                ResolveReceiveEpisodeStreamId(channel, streamId),
                streamId,
                sourceId,
                samples);
        }
        LogVocoderAudioLevel(channel, samples, ChannelAudioDirection.Receive, streamId);
    }

    private ChannelViewModel? ResolveReceiveRecordingTarget(ChannelViewModel decodedChannel)
    {
        SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
            decodedChannel.Definition.SystemName,
            StringComparison.OrdinalIgnoreCase));
        return system is null
            ? decodedChannel.IsRecordingEnabled ? decodedChannel : null
            : ReceiveRecordingTargetResolver.Resolve(decodedChannel, system.Channels);
    }

    private uint ResolveReceiveEpisodeStreamId(ChannelViewModel channel, uint physicalStreamId)
        => receiveCallEpisodes.TryGet(
            channel.Definition.SystemName,
            ProtocolFor(channel),
            physicalStreamId,
            out ReceiveCallEpisodeSnapshot? episode)
            ? episode.PrimaryStreamId
            : physicalStreamId;

    private ReceivePlaybackEpisode ResolveReceivePlaybackEpisode(
        ChannelViewModel channel,
        FneTrafficFrame traffic)
        => receiveCallEpisodes.TryGet(
            channel.Definition.SystemName,
            traffic.Protocol,
            traffic.StreamId,
            out ReceiveCallEpisodeSnapshot? episode)
            ? new ReceivePlaybackEpisode(
                episode.EpisodeId,
                episode.PrimaryStreamId,
                traffic.StreamId,
                RetainUntilEpisodeCompletion: true)
            : new ReceivePlaybackEpisode(
                -checked((long)traffic.StreamId),
                traffic.StreamId,
                traffic.StreamId,
                RetainUntilEpisodeCompletion: false);

    private void HandlePresentedReceiveSamples(
        ChannelViewModel channel,
        uint streamId,
        ReadOnlyMemory<short> samples,
        TimeSpan presentationDelay)
    {
        bool meterWasIdle = audioMeterPipeline.Observe(
            channel,
            streamId,
            samples.Span,
            ChannelAudioDirection.Receive,
            presentationDelay);
        if (meterWasIdle)
            StartAudioMeterTimer();
    }

    private void HandleTransmitSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        callRecordings.WriteTransmitSamples(channel, streamId, sourceId, samples);
        if (audioMeterPipeline.Observe(
                channel,
                streamId,
                samples.Span,
                ChannelAudioDirection.Transmit))
        {
            StartAudioMeterTimer();
        }
        LogVocoderAudioLevel(channel, samples, ChannelAudioDirection.Transmit, streamId);
    }

    private void LogVocoderAudioLevel(
        ChannelViewModel channel,
        ReadOnlyMemory<short> samples,
        ChannelAudioDirection direction,
        uint streamId = 0)
    {
        if (!verboseDiagnosticLogging || samples.IsEmpty)
            return;

        IReadOnlyList<PcmLevelMeasurement> measurements;
        lock (audioLevelLogSync)
        {
            var key = (channel, direction);
            if (!audioLevelLogs.TryGetValue(key, out PcmLevelLogState? state))
            {
                state = new PcmLevelLogState(streamId);
                audioLevelLogs.Add(key, state);
            }
            else if (streamId != 0 && state.StreamId != streamId)
            {
                state.Reset(streamId);
            }

            measurements = state.Levels.Observe(samples.Span);
            if (measurements.Count == 0)
                return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        string streamText = streamId == 0 ? string.Empty : $", stream {streamId}";
        foreach (PcmLevelMeasurement measurement in measurements)
        {
            AddDebugLog(
                now,
                channel.Definition.SystemName,
                DebugLogSeverity.Debug,
                $"Vocoder {direction.ToString().ToUpperInvariant()} {ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                $"on {channel.Name}: PCM RMS {measurement.RmsDbfs:0.0} dBFS, " +
                $"peak {measurement.PeakDbfs:0.0} dBFS over " +
                $"{FormatAudioLevelDuration(measurement.SampleCount)}{streamText}.");
        }
    }

    internal static string FormatAudioLevelDuration(long sampleCount)
    {
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        return $"{sampleCount / (double)VoiceSampleRate:0.0##} s";
    }

    private void HandleAudioMeterTick(object? sender, EventArgs e)
    {
        foreach (ChannelAudioMeterUpdate update in audioMeterPipeline.Advance())
        {
            if (update.Direction == ChannelAudioDirection.Receive)
                update.Channel.SetPresentedReceiveAudioLevel(update.Level, update.PeakLevel);
            else
                update.Channel.SetAudioLevel(
                    update.Level,
                    update.Direction,
                    update.StreamId,
                    update.PeakLevel);
        }

        if (!audioMeterPipeline.HasActivity)
            audioMeterTimer.Stop();
    }

    private void StartAudioMeterTimer()
        => TaskObservation.Observe(
            uiDispatcher.InvokeAsync(audioMeterTimer.Start).AsTask());

    private void ObservePatchDecodedSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        patchForwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples);
    }

    private async Task SyncPatchSourceDecodeAsync()
    {
        try
        {
            await patchSourceDecode.ApplyChannelsAsync(GetActivePatchSourceChannels()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            uiDispatcher.Post(() =>
                AudioStatusText = $"Patch source decode unavailable: {exception.Message}");
        }
    }

    private ChannelViewModel[] GetActivePatchSourceChannels()
        => PatchSourceSelectionPolicy.SelectEnabledSources(PatchGroups);

    private sealed class PcmLevelLogState(uint streamId)
    {
        public uint StreamId { get; private set; } = streamId;
        public PcmLevelWindowAccumulator Levels { get; } =
            new(VocoderAudioLevelWindowSamples);

        public void Reset(uint nextStreamId)
        {
            StreamId = nextStreamId;
            Levels.Reset();
        }
    }

    private async Task ProcessPatchSourceAsync(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        try
        {
            await patchSourceDecode.ProcessAsync(channel, traffic).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            uiDispatcher.Post(() =>
                AudioStatusText = $"Patch source decode stopped: {exception.Message}");
        }
    }

    private void EnqueuePatchSource(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        patchSourceReceiveWork.Start(channel);
        patchSourceReceiveWork.Enqueue(channel, traffic, out _);
    }

    private bool ShouldRecordSource(ChannelViewModel channel, uint sourceId)
    {
        return !userSettings.RecordingIgnoredSubscriberIds.TryGetValue(
                channel.SettingsKey,
                out List<uint>? ignoredSubscriberIds) ||
            !ignoredSubscriberIds.Contains(sourceId);
    }

    private void HandleRecordingFaulted(ChannelViewModel channel, Exception exception)
    {
        uiDispatcher.Post(() =>
        {
            channel.SetRecordingEnabled(false);
            AudioStatusText = $"TAR recording stopped: {exception.Message}";
        });
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        uiDispatcher.Post(() =>
        {
            if (result.Metadata is CallRecordingMetadata metadata && metadata.IsPlayable)
            {
                RecordRecordingCatalogMutation();
                CallRecordingMetadata? existing = recordingEntries.FirstOrDefault(candidate =>
                    candidate.FilePath.Equals(metadata.FilePath, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    recordingEntries.Remove(existing);
                recordingEntries.Insert(0, metadata);
                callHistory.AddOrAttachRecording(metadata);

                NotifyCallHistoryChanged();
            }
            else if (result.Error is null && !string.IsNullOrWhiteSpace(result.Diagnostic))
            {
                AudioStatusText = $"TAR recording skipped: {result.Diagnostic}";
            }
        });
    }

    private void HandleRecordingPlaybackFaulted(Exception exception)
    {
        uiDispatcher.Post(() =>
            AudioStatusText = $"Recording playback stopped: {exception.Message}");
    }

    private void HandleRecordingPlaybackStateChanged(
        object? sender,
        RecordingPlaybackStateChangedEventArgs e)
    {
        uiDispatcher.Post(() =>
        {
            foreach (CallHistoryEntry entry in callHistory.Entries)
            {
                entry.SetRecordingPlaying(
                    e.IsPlaying && RecordingPathEquals(entry.RecordingPath, e.Path));
            }
        });
    }

    private static bool RecordingPathEquals(string candidatePath, string playbackPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            return Path.GetFullPath(candidatePath).Equals(
                playbackPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void RefreshRecordings(bool pruneExpired = false)
    {
        RecordingCatalogScanSnapshot snapshot = historyRecording.BeginRecordingCatalogScan();
        Task scan = RefreshRecordingsAsync(snapshot, pruneExpired);
        historyRecording.PublishRecordingCatalogScan(snapshot, scan);
    }

    private async Task RefreshRecordingsAsync(
        RecordingCatalogScanSnapshot snapshot,
        bool pruneExpired)
    {
        CancellationToken cancellationToken = snapshot.CancellationToken;
        try
        {
            RecordingCatalogScanResult catalog = await callRecordings
                .LoadAndPruneRecordingsAsync(
                    pruneExpired,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ObserveRecordingCatalogHealth(catalog);
            cancellationToken.ThrowIfCancellationRequested();
            bool applied = await ApplyRecordingCatalogAsync(
                catalog.Recordings,
                snapshot).ConfigureAwait(false);
            if (!applied && !cancellationToken.IsCancellationRequested)
            {
                if (historyRecording.ShouldRestartRecordingCatalogScan(snapshot))
                    RefreshRecordings();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await RunOnUiThreadAsync(() =>
                AudioStatusText = $"Unable to refresh recording catalog: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task<bool> ApplyRecordingCatalogAsync(
        IReadOnlyList<CallRecordingMetadata> loaded,
        RecordingCatalogScanSnapshot snapshot)
    {
        return await ApplyRecordingCatalogUiBatchAsync(snapshot, () =>
        {
            historyRecording.ReplaceRecordingEntries(loaded);
            callHistory.ReplaceRecordingCatalog(loaded);
            NotifyCallHistoryChanged();
        }).ConfigureAwait(false);
    }

    private async Task<bool> ApplyRecordingCatalogUiBatchAsync(
        RecordingCatalogScanSnapshot snapshot,
        Action action)
    {
        bool applied = false;
        await RunOnUiThreadAsync(() =>
        {
            applied = historyRecording.TryApplyRecordingCatalogSnapshot(snapshot, action);
        }).ConfigureAwait(false);
        return applied;
    }

    private void RecordRecordingCatalogMutation()
        => historyRecording.RecordRecordingCatalogMutation();

    internal static bool IsRecordingCatalogSnapshotCurrent(
        int snapshotGeneration,
        int currentGeneration,
        long snapshotMutationRevision,
        long currentMutationRevision,
        bool isCancellationRequested)
        => !isCancellationRequested &&
           snapshotGeneration == currentGeneration &&
           snapshotMutationRevision == currentMutationRevision;

    private void ApplyRecordingRetention()
    {
        if (!int.TryParse(
                RecordingRetentionDaysText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int days) ||
            days < 0 || days > 3650)
        {
            TransmitStatusText = "Recording retention must be a whole number from 0 to 3650 days; 0 disables pruning.";
            return;
        }

        userSettings.RecordingRetentionDays = days;
        callRecordings.RetentionDays = days;
        PersistUserSettings();
        RefreshRecordings(pruneExpired: true);
        RecordingRetentionDaysText = days.ToString(CultureInfo.InvariantCulture);
        AudioStatusText = days == 0
            ? "TAR retention pruning disabled."
            : $"TAR retention set to {days} day(s).";
    }


    private void HandlePttSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandleHistoryRecordingPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandleAudioSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandleToneWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandlePttSourceStateChanged(object? sender, PttSourceStateChange change)
        => DispatchKeyboardPttStateChanged(change.Pressed, change.Scope, change.Source);

    private void DispatchKeyboardPttStateChanged(
        bool pressed,
        PttTargetScope scope,
        PttActivationSource source)
    {
        if (uiDispatcher.CheckAccess())
            TaskObservation.Observe(HandleKeyboardPttStateChangedAsync(pressed, scope, source));
        else
            uiDispatcher.Post(
                () => TaskObservation.Observe(HandleKeyboardPttStateChangedAsync(pressed, scope, source)));
    }

    private async Task HandleKeyboardPttStateChangedAsync(
        bool pressed,
        PttTargetScope scope,
        PttActivationSource source)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        // Starting TX includes microphone readiness and the required permit
        // cue. Keep later toggle/release edges from stopping the shared TX
        // path until that startup sequence (including cue drainage) finishes.
        await pttStateChangeLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            if (pressed)
            {
                ObservePttActivationSource(source);
                await StartScopedPttAsync(scope);
            }
            else
            {
                await StopScopedPttAsync();
            }
        }
        finally
        {
            pttStateChangeLock.Release();
        }
    }

    private async Task StartScopedPttAsync(PttTargetScope scope)
    {
        IReadOnlyList<ChannelViewModel> targets = GetSelectedTransmitTargets(scope);
        if (targets.Count == 0)
        {
            TransmitStatusText = scope == PttTargetScope.ActiveSystem
                ? $"Choose TX on one or more cards in {SelectedSystemName} before using {ActiveSystemPttKeyText}."
                : $"Choose TX on one or more cards before using {GlobalPttKeyText}.";
            return;
        }
        if (transmitCoordinator.ActiveChannel is not null)
            return;

        await StartTransmitAsync(targets);

        // A press-and-hold source can be released while microphone readiness
        // and the permit cue are still completing. Finish that indication,
        // then stop the call instead of racing the queued release edge.
        if (!AnyPttSourcePressed && transmitCoordinator.ActiveChannel is not null)
            await StopTransmitAsync(transmitCoordinator.ActiveChannels);
    }

    private async Task StopScopedPttAsync()
    {
        if (AnyPttSourcePressed)
            return;

        ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
        if (active.Length > 0)
            await StopTransmitAsync(active);
    }

    private bool AnyPttSourcePressed
        => pttSession.IsAnySourcePressed;

    private string SerialPttScopeText => userSettings.SerialPttActiveSystemOnly
        ? "TX-selected resources in the active system"
        : "all TX-selected resources";

    private PttTargetScope GetSerialPttTargetScope()
        => userSettings.SerialPttActiveSystemOnly
            ? PttTargetScope.ActiveSystem
            : PttTargetScope.AllSelectedResources;

    private string DescribeKeyboardPttReadiness(
        KeyboardPttStartResult globalResult,
        KeyboardPttStartResult activeSystemResult)
    {
        string global = DescribeKeyboardPttBinding(
            "global",
            GlobalPttKeyText,
            globalResult);
        string activeSystem = DescribeKeyboardPttBinding(
            "active-system",
            ActiveSystemPttKeyText,
            activeSystemResult);
        return $"PTT idle; {global}; {activeSystem}.";
    }

    private static string DescribeKeyboardPttBinding(
        string scope,
        string keyText,
        KeyboardPttStartResult result)
        => result.Availability switch
        {
            KeyboardPttAvailability.Disabled => $"{scope} keyboard PTT disabled",
            KeyboardPttAvailability.OsGlobal => $"OS-global {scope} {keyText} ready",
            _ when result.GlobalCaptureError is not null =>
                $"{scope} {keyText} using window fallback ({result.GlobalCaptureError.Message})",
            _ => $"{scope} {keyText} using window fallback"
        };

    private static int ReadSerialPttBaudRate()
    {
        string? configured = Environment.GetEnvironmentVariable("DVM_PTT_SERIAL_BAUD");
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baudRate) && baudRate > 0
            ? baudRate
            : 9_600;
    }

    private static KeyboardPttKey ParseGlobalPttKey(string? value)
        => Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key)
            ? key
            : KeyboardPttKey.None;

    private void HandleTransmitFaulted(object? sender, Exception exception)
    {
        ObserveTransmitHealthError(exception);
        ChannelViewModel[] channels = transmitCoordinator.ActiveChannels.ToArray();
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (channel, transmitCoordinator.GetActiveStreamId(channel)))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        uiDispatcher.Post(() =>
        {
            foreach (ChannelViewModel channel in channels)
                channel.SetTransmitEnabled(false);
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = null;
            TransmitStatusText = $"Transmission stopped: {exception.Message}";
        });
        TaskObservation.Observe(Task.Run(async () =>
        {
            try
            {
                await transmitCoordinator.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // The original fault is already reported to the operator.
            }
            finally
            {
                foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
                {
                    callRecordings.StopTransmit(channel);
                    SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Channels.Contains(channel));
                    if (system is not null)
                        callHistory.CompleteConsoleTransmission(
                            system.Name,
                            ProtocolFor(channel),
                            streamId,
                            DateTimeOffset.Now,
                            channel.Name,
                            channel.Definition.DestinationId);
                }
                if (activeStreams.Length > 0)
                    uiDispatcher.Post(NotifyCallHistoryChanged);
            }
            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                uiDispatcher.Post(() =>
                    TransmitStatusText = $"Transmission stopped; audio recovery failed: {cleanupException.Message}");
            }
        }));
    }

    private void SetBusy(bool value)
    {
        busy = value;
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseGeneratedAudioCanExecuteChanged();
    }

    private void NotifyConnectionPresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionCommand)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionPillText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystemName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemStatusText)));
    }

    private void RecordSubscriberCommandAudit(
        string systemName,
        P25SubscriberCommand command,
        uint destinationId,
        bool succeeded,
        string detail)
    {
        if (subscriberCommandAudit.Count >= MaximumSubscriberCommandAuditEntries)
            subscriberCommandAudit.RemoveAt(subscriberCommandAudit.Count - 1);

        subscriberCommandAudit.Insert(0, new SubscriberCommandAuditEntry(
            DateTimeOffset.UtcNow,
            systemName,
            command,
            destinationId,
            succeeded,
            detail));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivitySubscriberCommandAudit)));
    }

    private static string CommandName(P25SubscriberCommand command)
        => command switch
        {
            P25SubscriberCommand.CallAlert => "Page",
            P25SubscriberCommand.RadioCheck => "Radio check",
            P25SubscriberCommand.Inhibit => "Inhibit",
            P25SubscriberCommand.Uninhibit => "Uninhibit",
            _ => command.ToString()
        };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void LoadUserBackground(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
            return;
        }

        try
        {
            userBackgroundBitmap = new Bitmap(path);
            mainBackgroundBrush = new ImageBrush(userBackgroundBitmap)
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.22
            };
        }
        catch
        {
            userBackgroundBitmap = null;
            mainBackgroundBrush = CreateShellBackgroundBrush(userSettings.DarkMode);
        }
    }

    private static IBrush CreateShellBackgroundBrush(bool darkMode)
        => new SolidColorBrush(Color.Parse(darkMode ? "#0D1116" : "#F3F5F7"));

    private void RestoreChannelWidgetLayout()
    {
        ApplyDefaultChannelWidgetLayout();
        foreach (ChannelViewModel channel in Zones.SelectMany(zone => zone.Channels).Distinct())
        {
            if (userSettings.ChannelWidgetPositions.TryGetValue(channel.SettingsKey, out WidgetPositionSetting? position))
                channel.SetWidgetPosition(position.X, position.Y);
        }
    }

    private void ApplyDefaultChannelWidgetLayout()
    {
        foreach (ZoneViewModel zone in Zones)
        {
            double x = 0;
            double y = 0;
            foreach (ChannelViewModel channel in zone.Channels)
            {
                if (x > 0 && x + channel.CardWidth > DefaultWidgetCanvasWidth)
                {
                    x = 0;
                    y += ChannelCardHeight + ChannelWidgetSpacing;
                }

                channel.SetWidgetPosition(x, y);
                x += channel.CardWidth + ChannelWidgetSpacing;
            }
        }
    }

    private void HandleClockTick(object? sender, EventArgs e)
    {
        RefreshClock();
        if (networkDisabledDemo)
            return;

        ExpireStaleReceiveStates(DateTimeOffset.UtcNow);
        TaskObservation.Observe(ReconcileReceiveSessionsAsync());
    }

    private void HandleConnectionDiagnosticsTick(object? sender, EventArgs e)
    {
        foreach (SystemViewModel system in Systems)
        {
            system.PublishTrafficDiagnostics();
            RefreshJitterBufferTelemetry(system);
        }
    }

    internal void ExpireStaleReceiveStates(DateTimeOffset now)
    {
        bool callHistoryChanged = false;
        callHistoryChanged = ExpireStaleReceiveRoutes(now) || callHistoryChanged;
        callHistoryChanged = ExpireReceiveCallEpisodes(now) || callHistoryChanged;
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private bool ExpireReceiveCallEpisodes(DateTimeOffset now)
    {
        bool callHistoryChanged = false;
        foreach (ReceiveCallEpisodeSnapshot episode in receiveCallEpisodes.Advance(
                     now,
                     episode => !IsReceiveEpisodePhysicallyActive(episode)))
        {
            callHistoryChanged = callHistory.Complete(
                episode.SystemName,
                episode.Protocol,
                episode.PrimaryStreamId,
                episode.PresentationEndAt,
                receiveEpisodeId: episode.EpisodeId) || callHistoryChanged;
            AddDebugLog(
                now,
                episode.SystemName,
                DebugLogSeverity.Info,
                FormatReceiveEpisodeCompleted(episode));
            StopReceiveEpisodeRecording(episode);
        }
        return callHistoryChanged;
    }

    private string FormatReceiveEpisodeCompleted(ReceiveCallEpisodeSnapshot episode)
    {
        SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
            episode.SystemName,
            StringComparison.OrdinalIgnoreCase));
        string channelName = system?.Channels.FirstOrDefault(channel =>
            ProtocolFor(channel) == episode.Protocol &&
            channel.Definition.DestinationId == episode.DestinationId &&
            (episode.Protocol != FneTrafficProtocol.Dmr || channel.Definition.Slot == episode.Slot))?.Name ??
            episode.DestinationId.ToString(CultureInfo.InvariantCulture);
        double durationSeconds = Math.Max(
            0,
            (episode.PresentationEndAt - episode.StartedAt).TotalSeconds);
        return $"RX logical call episode ended on {channelName}: " +
            $"{episode.Protocol.ToString().ToUpperInvariant()} {episode.SourceId}→{episode.DestinationId}, " +
            $"episode {episode.EpisodeId}, {episode.StreamIds.Count} physical stream" +
            $"{(episode.StreamIds.Count == 1 ? string.Empty : "s")}, " +
            $"duration {durationSeconds:0.0} s.";
    }

    private bool IsReceiveEpisodePhysicallyActive(ReceiveCallEpisodeSnapshot episode)
    {
        SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
            episode.SystemName,
            StringComparison.OrdinalIgnoreCase));
        return system is not null && system.Channels.Any(channel =>
            episode.StreamIds.Any(channel.IsTrackingReceiveStream));
    }

    private void StopReceiveEpisodeRecording(ReceiveCallEpisodeSnapshot episode)
    {
        SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Name.Equals(
            episode.SystemName,
            StringComparison.OrdinalIgnoreCase));
        if (system is null)
            return;

        ChannelViewModel[] episodeChannels = system.Channels
            .Where(channel =>
                ProtocolFor(channel) == episode.Protocol &&
                channel.Definition.DestinationId == episode.DestinationId &&
                (episode.Protocol != FneTrafficProtocol.Dmr ||
                 channel.Definition.Slot == episode.Slot))
            .Distinct()
            .ToArray();
        if (episodeChannels.Length == 0)
            return;

        TaskObservation.Observe(
            receiveEpisodeCompletion.CompleteAsync(episode, episodeChannels),
            exception => ReportReceiveEpisodeCompletionFailure(episode, exception));
    }

    private void ReportReceiveEpisodeCompletionFailure(
        ReceiveCallEpisodeSnapshot episode,
        Exception exception)
    {
        if (exception is ObjectDisposedException && Volatile.Read(ref disposeStarted) != 0)
            return;

        DesktopCrashLog.Write("Receive episode completion", exception);
        TaskObservation.Observe(
            uiDispatcher.InvokeAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Warning,
                $"Unable to complete receive episode {episode.EpisodeId}: {exception}"))
                .AsTask(),
            reportingException => DesktopCrashLog.Write(
                "Receive episode completion UI reporting",
                reportingException));
    }

    private void NotifyCallHistoryChanged()
    {
        historyRecording.RefreshFilteredCallHistory();
        RefreshActivityCallHistory();
    }

    private void RefreshActivityCallHistory()
    {
        CallHistoryEntry[] desired = SelectActivityHistory(
            CallHistory,
            SelectedSystem?.Name,
            activityCurrentZoneOnly
                ? SelectedSystem?.SelectedZone?.Channels.Select(channel => channel.Name)
                : null,
            activityReceiveEnabledOnly
                ? SelectedSystem?.Channels
                    .Where(channel => channel.IsAudioEnabled)
                    .Select(channel => channel.Name)
                : null);
        historyRecording.RefreshActivityCallHistory(desired);
    }

    internal static CallHistoryEntry[] SelectActivityHistory(
        IEnumerable<CallHistoryEntry> history,
        string? selectedSystemName,
        IEnumerable<string>? selectedZoneChannelNames,
        IEnumerable<string>? receiveEnabledChannelNames = null)
    {
        if (selectedSystemName is null)
            return [];

        HashSet<string>? selectedChannels = selectedZoneChannelNames is null
            ? null
            : new HashSet<string>(selectedZoneChannelNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? receiveEnabledChannels = receiveEnabledChannelNames is null
            ? null
            : new HashSet<string>(receiveEnabledChannelNames, StringComparer.OrdinalIgnoreCase);
        return history
            .Where(entry => entry.SystemName.Equals(selectedSystemName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => selectedChannels is null || selectedChannels.Contains(entry.ChannelName))
            .Where(entry => receiveEnabledChannels is null || receiveEnabledChannels.Contains(entry.ChannelName))
            .Take(CallHistoryStore.DefaultMaxEntries)
            .ToArray();
    }

    internal static void SynchronizeHistoryView(
        ObservableCollection<CallHistoryEntry> target,
        IEnumerable<CallHistoryEntry> desiredEntries)
        => HistoryViewSynchronizer.Synchronize(target, desiredEntries);

    private void RefreshClock()
    {
        SetField(
            ref clockText,
            FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds),
            nameof(ClockText));
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        foreach (ToolbarClockViewModel clock in toolbarClocks)
            clock.Update(utcNow, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
    }

    internal static string FormatClock(DateTime value, bool use24HourTime, bool showSeconds)
    {
        string format = use24HourTime
            ? showSeconds ? "HH:mm:ss" : "HH:mm"
            : showSeconds ? "h:mm:ss tt" : "h:mm tt";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private static void ApplyTheme(bool darkMode)
    {
        if (Application.Current is not Application application)
            return;

        application.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        IReadOnlyDictionary<string, string> colors = darkMode
            ? new Dictionary<string, string>
            {
                ["ShellBackgroundBrush"] = "#0D1116",
                ["ShellHeaderBrush"] = "#1A2028",
                ["PrimaryTextBrush"] = "#DCE3EB",
                ["CardBackgroundBrush"] = "#151D26",
                ["MutedTextBrush"] = "#B7C0C9",
                ["ButtonBackgroundBrush"] = "#1A222D",
                ["ButtonHoverBrush"] = "#253446",
                ["ControlBorderBrush"] = "#273443",
                ["PttBackgroundBrush"] = "#17202B",
                ["SelectorBackgroundBrush"] = "#242938",
                ["TabTextBrush"] = "#AEB9C5",
                ["SelectedTabTextBrush"] = "#F4F7FA",
                ["SidebarBackgroundBrush"] = "#151C25",
                ["ActivityBackgroundBrush"] = "#1C2530",
                ["StatusBarBackgroundBrush"] = "#1A2028",
                ["SplitterBrush"] = "#25313D",
                ["ClockTextBrush"] = "#FFFFFF",
                ["ClockBorderBrush"] = "#3A4654",
                ["WarningBackgroundBrush"] = "#332A1A",
                ["WarningBorderBrush"] = "#7A5C28"
            }
            : new Dictionary<string, string>
            {
                ["ShellBackgroundBrush"] = "#F3F5F7",
                ["ShellHeaderBrush"] = "#E4E8EC",
                ["PrimaryTextBrush"] = "#18212B",
                ["CardBackgroundBrush"] = "#FFFFFF",
                ["MutedTextBrush"] = "#4D5965",
                ["ButtonBackgroundBrush"] = "#FFFFFF",
                ["ButtonHoverBrush"] = "#DDE5ED",
                ["ControlBorderBrush"] = "#8996A3",
                ["PttBackgroundBrush"] = "#E2E8EF",
                ["SelectorBackgroundBrush"] = "#E8EDF3",
                ["TabTextBrush"] = "#40505F",
                ["SelectedTabTextBrush"] = "#111820",
                ["SidebarBackgroundBrush"] = "#E9EEF3",
                ["ActivityBackgroundBrush"] = "#FFFFFF",
                ["StatusBarBackgroundBrush"] = "#E1E7ED",
                ["SplitterBrush"] = "#8996A3",
                ["ClockTextBrush"] = "#FFFFFF",
                ["ClockBorderBrush"] = "#65717D",
                ["WarningBackgroundBrush"] = "#FFF4D6",
                ["WarningBorderBrush"] = "#B47B18"
            };
        foreach (KeyValuePair<string, string> entry in colors)
            application.Resources[entry.Key] = new SolidColorBrush(Color.Parse(entry.Value));
    }

    private void PersistUserSettings()
    {
        try
        {
            userSettingsWriter.Schedule(userSettingsStore.CaptureSnapshot(userSettings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Operator state must never prevent the console from running.
        }
    }

    internal Task FlushUserSettingsAsync()
        => userSettingsWriter.FlushAsync();

    public void ApplyPatchGroup(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(members);

        string normalizedName = groupName.Trim();
        List<PatchMemberAddress> normalizedMembers = members
            .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
            .Select(member => new PatchMemberAddress(
                member.SystemName,
                member.DestinationId,
                member.ChannelName))
            .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        PersistGroupDefinition(normalizedName, normalizedMembers, enabled, oneWay);
        ReapplyPatchState();
        PersistUserSettings();
        RefreshPatchMembershipConflicts();
        TaskObservation.Observe(SyncPatchSourceDecodeAsync());
    }

    public void ApplyPatchGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<PatchMemberAddress> members = group.GetMembersInRoutingOrder()
            .Select(member => PatchMemberResolver.FromChannel(member.Channel))
            .ToList();
        if (group.IsMultiSelect)
        {
            if (ReferenceEquals(activeMultiSelectGroup, group))
            {
                StatusText = $"Stop multi-select PTT for '{group.Name}' before changing its membership.";
                return;
            }
            if (group.GetMembershipValidationError() is { } validationError)
            {
                StatusText = $"Multi-select group '{group.Name}' was not saved. {validationError}";
                return;
            }
            PersistGroupDefinition(group.Name, members, enabled: true, oneWay: false);
            PersistUserSettings();
            RefreshPatchMembershipConflicts();
            StatusText = $"Multi-select group '{group.Name}' saved with {members.Count} member(s).";
            return;
        }

        if (group.GetMembershipValidationError() is { } patchValidationError)
        {
            StatusText = $"Patch group '{group.Name}' was not saved. {patchValidationError}";
            return;
        }

        ApplyPatchGroup(group.Name, members, group.IsEnabled, group.IsOneWay);
        RefreshPatchMembershipConflicts();
        StatusText = $"Patch group '{group.Name}' {(group.IsEnabled ? "enabled" : "disabled")}.";
    }

    public void SetPatchGroupEnabled(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.IsPatchGroup)
            return;
        codeplugGroupState.EnabledStates[group.Name] = group.IsEnabled;
        ReapplyPatchState();
        PersistUserSettings();
        TaskObservation.Observe(SyncPatchSourceDecodeAsync());
        StatusText = $"Patch group '{group.Name}' {(group.IsEnabled ? "enabled" : "disabled")}.";
    }

    public async Task ToggleMultiSelectPttAsync(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.IsMultiSelect)
            return;

        if (group.IsPttActive)
        {
            ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
            if (active.Length > 0)
                await StopTransmitAsync(active).ConfigureAwait(false);
            group.SetPttActive(false);
            if (ReferenceEquals(activeMultiSelectGroup, group))
                activeMultiSelectGroup = null;
            return;
        }

        if (transmitCoordinator.ActiveChannels.Count > 0)
        {
            TransmitStatusText = "Stop the current transmission before starting multi-select PTT.";
            return;
        }

        ChannelViewModel[] targets = group.Members
            .Where(member => member.IsMember && member.CanTransmit)
            .Select(member => member.Channel)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
        {
            TransmitStatusText = $"Multi-select group '{group.Name}' has no transmit-capable members.";
            return;
        }

        ObservePttActivationSource(PttActivationSource.LocalChannelControl);
        await StartTransmitAsync(targets).ConfigureAwait(false);
        if (transmitCoordinator.ActiveChannels.Count == targets.Length)
        {
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = group;
            group.SetPttActive(true);
        }
    }

    private void PersistGroupDefinition(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        string normalizedName = groupName.Trim();
        codeplugGroupState.Memberships[normalizedName] = members
            .Select(PatchMemberResolver.ToSetting)
            .ToList();
        codeplugGroupState.OneWayModes[normalizedName] = oneWay;
        codeplugGroupState.EnabledStates[normalizedName] = enabled;
    }

    private IReadOnlyList<PatchGroupEditorViewModel> BuildPatchGroups(
        IEnumerable<GroupConfiguration> groupDefinitions)
    {
        IReadOnlyList<ChannelViewModel> channels = Systems
            .SelectMany(system => system.Channels)
            .ToArray();
        var memberResolver = new PatchMemberResolver(channels);
        List<PatchGroupEditorViewModel> groups = [];
        foreach (GroupConfiguration definition in groupDefinitions)
        {
            string groupName = definition.Name.Trim();
            if (groupName.Length == 0)
                continue;

            List<PatchMemberSetting> savedMembers = codeplugGroupState.Memberships
                .TryGetValue(groupName, out List<PatchMemberSetting>? configuredSettings)
                ? configuredSettings ?? []
                : [];
            List<ChannelViewModel> resolvedMembers = savedMembers
                .Select(memberResolver.Resolve)
                .Where(channel => channel is not null)
                .Cast<ChannelViewModel>()
                .Distinct()
                .ToList();
            HashSet<string> configuredMembers = resolvedMembers
                .Select(channel => PatchMemberResolver.FromChannel(channel).Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? configuredSourceKey = resolvedMembers.FirstOrDefault() is { } savedSource
                ? PatchMemberResolver.FromChannel(savedSource).Key
                : null;
            bool isMultiSelect = definition.IsMultiselectGroup();
            bool enabled = isMultiSelect ||
                (codeplugGroupState.EnabledStates.TryGetValue(groupName, out bool savedEnabled) && savedEnabled);
            bool oneWay = codeplugGroupState.OneWayModes.TryGetValue(groupName, out bool savedOneWay) && savedOneWay;
            var group = new PatchGroupEditorViewModel(
                groupName,
                enabled,
                oneWay,
                channels.Select(channel => new PatchMemberEditorViewModel(
                    channel,
                    configuredMembers.Contains(PatchMemberResolver.FromChannel(channel).Key))),
                isMultiSelect,
                configuredSourceKey);
            group.MembershipChanged += HandlePatchMembershipChanged;
            groups.Add(group);
        }

        return groups;
    }

    private void HandlePatchMembershipChanged(object? sender, EventArgs args)
        => RefreshPatchMembershipConflicts();

    private void RefreshPatchMembershipConflicts()
    {
        Dictionary<string, List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>> memberships =
            PatchGroups
                .SelectMany(group => group.Members
                    .Where(member => member.IsMember)
                    .Select(member => (Group: group, Member: member)))
                .GroupBy(item => item.Member.Channel.SettingsKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (PatchGroupEditorViewModel group in PatchGroups)
        {
            List<string> conflictingChannels = [];
            foreach (PatchMemberEditorViewModel member in group.Members)
            {
                if (!member.IsMember ||
                    !memberships.TryGetValue(member.Channel.SettingsKey, out List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>? owners) ||
                    owners.Count < 2)
                {
                    member.SetConflictText(null);
                    continue;
                }

                string otherGroups = string.Join(", ", owners
                    .Where(owner => !ReferenceEquals(owner.Group, group))
                    .Select(owner => owner.Group.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                member.SetConflictText($"Also assigned to: {otherGroups}");
                conflictingChannels.Add(member.Channel.Name);
            }

            group.SetConflictSummary(conflictingChannels.Count == 0
                ? null
                : $"{conflictingChannels.Count} member overlap(s): {string.Join(", ", conflictingChannels.Distinct(StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private void RestorePatchState(IEnumerable<GroupConfiguration>? groupDefinitions)
    {
        if (!userSettings.RetainPatchStateOnStartup || groupDefinitions is null)
            return;

        ReapplyPatchState(groupDefinitions);
    }

    private void ReapplyPatchState(IEnumerable<GroupConfiguration>? groupDefinitions = null)
    {
        IEnumerable<string> configuredPatchNames = groupDefinitions is not null
            ? groupDefinitions
                .Where(group => group.IsPatchGroup())
                .Select(group => group.Name.Trim())
            : PatchGroups
                .Where(group => group.IsPatchGroup)
                .Select(group => group.Name);
        HashSet<string> patchGroupNames = configuredPatchNames
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memberResolver = new PatchMemberResolver(Systems.SelectMany(system => system.Channels));
        var memberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> entry in codeplugGroupState.Memberships)
        {
            if (!patchGroupNames.Contains(entry.Key))
                continue;
            if (!codeplugGroupState.EnabledStates.TryGetValue(entry.Key, out bool enabled) || !enabled)
                continue;

            memberships[entry.Key] = entry.Value
                .Select(memberResolver.Resolve)
                .Where(channel => channel is not null)
                .Cast<ChannelViewModel>()
                .Select(PatchMemberResolver.FromChannel)
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        patchForwarding.ApplyMemberships(memberships, codeplugGroupState.OneWayModes);
    }

    internal void RecordLoadedCodeplug(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        userSettings.LastCodeplugPath = normalizedPath;
        userSettings.RecentCodeplugPaths = new[] { normalizedPath }
            .Concat(userSettings.RecentCodeplugPaths ?? [])
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(UserSettings.MaximumRecentCodeplugs)
            .ToList();
        recentCodeplugPaths.Clear();
        foreach (string recentPath in userSettings.RecentCodeplugPaths)
            recentCodeplugPaths.Add(recentPath);
        if (selectedChannel is not null &&
            !Systems.SelectMany(system => system.Channels).Contains(selectedChannel))
        {
            selectedChannel = null;
            selectedSystem = null;
            userSettings.LastSelectedSystemName = null;
            userSettings.LastSelectedChannelKey = null;
        }
        PersistUserSettings();
    }

    private string GetDefaultRecordingRoot(string? configuredRootPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRootPath))
            return Path.GetFullPath(configuredRootPath.Trim());

        string settingsPath = userSettingsStore.Path;
        string? settingsDirectory = Path.GetDirectoryName(settingsPath);
        return Path.Combine(settingsDirectory ?? AppContext.BaseDirectory, "Recordings");
    }
}
