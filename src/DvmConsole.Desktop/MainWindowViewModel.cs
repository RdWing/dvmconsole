// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Ptt;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Presentation;
using DvmConsole.Storage;
using DvmConsole.Vocoder;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable,
    ICallHistoryViewModel,
    IRecordingCommandSession
{
    internal const double ChannelWidgetSpacing = 8;
    internal const double DefaultWidgetCanvasWidth = 900;
    private const int VoiceSampleRate = 8_000;
    private const int VocoderAudioLevelWindowSamples = VoiceSampleRate;
    private const string DvmConsoleProcessingDisplay = "DVM Console processing";
    private const string WindowsCommunicationsProcessingDisplay = "Windows communications processing";
    private static readonly string[] WindowsAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay, WindowsCommunicationsProcessingDisplay];
    private static readonly string[] DvmConsoleAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay];
    private readonly ConsoleOperationalRuntime operationalRuntime;
    private ConsoleReceiveRuntime receiveRuntime => operationalRuntime.Receive;
    private ChannelAudioSettingsController channelAudioSettings => operationalRuntime.AudioSettings;
    private ChannelReceiveAudioCoordinator audioCoordinator => receiveRuntime.Audio;
    private readonly ApplicationAudioBackendProvider audioBackendProvider;
    private ChannelReceiveWorkQueue receiveAudioWork => receiveRuntime.Work;
    private ReceiveSessionController receiveSessions => receiveRuntime.Sessions;
    private readonly SingleFlightAsyncAction receiveSessionReconciler;
    private ReceiveEpisodeRetirement receiveEpisodeRetirement => operationalRuntime.EpisodeRetirement;
    private ConsolePatchRuntime patchRuntime => operationalRuntime.Patches;
    private ChannelReceiveWorkQueue patchSourceReceiveWork => patchRuntime.Work;
    private readonly UserSettingsStore userSettingsStore;
    private readonly IAssetStore assetStore;
    private readonly IAudioBackendFactory audioBackendFactory;
    private readonly IVocoderFactory vocoderFactory;
    private readonly IDesktopPrivacyPermissionService privacyPermissionService;
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channelsById;
    private ConsoleChannelMediaDirectory channelMedia => operationalRuntime.Media;
    private ConsoleTransmitChannelDirectory transmitChannels => operationalRuntime.TransmitChannels;
    private ChannelTransmitControlCommands transmitControls => operationalRuntime.TransmitControls;
    private TalkgroupAuthorityController talkgroupAuthority => operationalRuntime.Authority;
    private TransmitTargetResolver transmitTargets => operationalRuntime.TransmitTargets;
    private readonly ChannelId[] systemChannelIds;
    private ConsoleRecordingRuntime recordingRuntime => operationalRuntime.Recording;
    private ChannelRecordingCommands recordingControls => operationalRuntime.RecordingControls;
    private ReceiveRecordingTargetIndex receiveRecordingTargets => recordingRuntime.Targets;

    private readonly UserSettings userSettings;
    private readonly ConsoleChannelSettingsPersistence channelPreferences;
    private readonly PreparedLiveSession? preparedSession;
    private readonly ShellSettingsController shellSettings;
    private readonly string loadedCodeplugPath;
    private readonly ConfigurationReference? configurationReference;
    private readonly CodeplugGroupState transientGroupState = new();
    private CodeplugGroupState codeplugGroupState
        => string.IsNullOrWhiteSpace(loadedCodeplugPath)
            ? transientGroupState
            : CodeplugGroupStateStore.GetOrMigrate(userSettings, loadedCodeplugPath);
    private readonly string codeplugDiagnosticsText;
    private ConsoleTransmitRuntime transmitRuntime => operationalRuntime.Transmit;
    private ConsoleTransmitState transmitState => operationalRuntime.TransmitState;
    private ChannelTransmitCoordinator transmitCoordinator => transmitRuntime.Microphone;
    private readonly DefaultAudioDeviceMonitor defaultAudioDeviceMonitor;
    private readonly LatestBooleanStateReconciler warmMicrophoneReconciler;
    private ToneTransmitCoordinator toneTransmitCoordinator => transmitRuntime.Tones;
    private LocalTonePlayer localTonePlayer => transmitRuntime.LocalTones;
    private TransmitAudioTransitionController transmitAudioTransition => transmitRuntime.AudioTransition;
    private TransmitLifecycleCoordinator transmitLifecycle => transmitRuntime.Lifecycle;
    private GeneratedAudioOperation generatedAudioOperation => transmitRuntime.GeneratedOperation;
    internal IGeneratedAudioOperationPort GeneratedAudioPort => transmitRuntime;
    private readonly CoalescedUiAction toneTargetRefresh;
    private readonly PatchRoutingController patchRouting;
    private PatchForwardingCoordinator patchForwarding => patchRuntime.Forwarding;
    private PatchSourceDecodeCoordinator patchSourceDecode => patchRuntime.Decoder;
    private readonly P25KeyRing? p25KeyRing;
    private readonly DmrKeyRing? dmrKeyRing;
    private readonly NxdnKeyRing? nxdnKeyRing;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly SemaphoreSlim pttStateChangeLock = new(1, 1);
    private readonly SemaphoreSlim transmitAdmissionGate = new(1, 1);
    private readonly PttSettingsViewModel pttSettings;
    private readonly PttSessionController pttSession;
    private readonly HardwarePttSequencer hardwarePttSequencer;
    private readonly HistoryRecordingController historyRecording;
    private readonly HistoryDiagnosticsController historyDiagnostics;
    private CallHistoryStore callHistory => historyRecording.History;
    internal IReadOnlyList<ConsoleCallHistoryRecord> ApplicationHistory => callHistory.ApplicationHistory;
    internal ConsoleSnapshotContextSource CreateSnapshotContextSource()
        => new(callRecordings, receiveOutput.GetEffectiveMuteReason, () => ReceiveMute.GloballyMuted,
            () => RecordingPlaybackState.Snapshot.Channel, () => patchRouting.MembershipIndex);
    private ObservableCollection<CallRecordingMetadata> recordingEntries
        => historyRecording.RecordingEntries;
    private readonly ToneWorkspaceViewModel toneWorkspace;
    private readonly TonePresentationController tonePresentation;
    private ObservableCollection<ToneSequenceStepViewModel> toneSequenceSteps
        => toneWorkspace.MutableToneSequenceSteps;
    private ObservableCollection<AlertToneViewModel> alertTones
        => toneWorkspace.MutableAlertTones;
    private readonly ObservableCollection<ToolbarClockViewModel> toolbarClocks = [];
    private readonly AudioSettingsViewModel audioSettings;
    private readonly AudioInputSettingsController audioInputSettingsController;
    private ObservableCollection<RxAudioProcessingModeViewModel> rxAudioProcessingModes
        => audioSettings.MutableRxAudioProcessingModes;
    private ObservableCollection<AudioDeviceOptionViewModel> audioInputDevices
        => audioSettings.MutableAudioInputDevices;
    private ObservableCollection<AudioDeviceOptionViewModel> audioOutputDevices
        => audioSettings.MutableAudioOutputDevices;
    private readonly DebugLogWorkspace debugLogs;
    private bool verboseDiagnosticLogging;
    private readonly ObservableCollection<string> recentCodeplugPaths = [];
    private readonly WebStreamOperatorController webStreamOperator;
    private readonly IUiDispatcher uiDispatcher;
    private readonly OperatorUndoController operatorUndo;
    private readonly SessionUiCallbackGate sessionUiCallbacks;
    private readonly SessionTerminalFence terminalFence;
    private readonly ChannelPcmLevelTracker audioLevelLogs = new(VocoderAudioLevelWindowSamples);
    private ChannelAudioMeterRuntime audioMeterPipeline => operationalRuntime.Meters;
    private readonly object audioMeterSync = new();
    private ConsoleReceiveDiagnostics receiveDiagnostics => operationalRuntime.ReceiveDiagnostics;
    private ReceiveBufferingRuntime receiveBufferingRuntime => operationalRuntime.Buffering;
    private ReceiveJitterBufferEffectivenessTracker receiveJitterEffectiveness => operationalRuntime.JitterEffectiveness;
    private readonly ReceiveCallEpisodeTracker receiveCallEpisodes;
    private readonly ReceiveOutputMutePolicy receiveOutputMutePolicy;
    private readonly SemaphoreSlim audioReconfigurationLock = new(1, 1);
    private ReceiveOutputController receiveOutput => receiveRuntime.Output;

    private ReceiveIngressCoordinator receiveTraffic => operationalRuntime.Traffic?.Ingress!;
    private readonly IReadOnlyDictionary<SystemId, (SystemViewModel View, ReceiveIngressSystem Runtime)> receiveSystems;
    private ReceiveChannelTrafficCoordinator receiveChannelTraffic => operationalRuntime.Traffic?.Channels!;
    private RadioSessionIngressCoordinator radioIngress => operationalRuntime.RadioIngress;
    private readonly ConnectionChimeTracker connectionChimeTracker = new();
    private ConnectionSessionController connectionSession => operationalRuntime.Connections;
    private readonly P25KeyRetrievalCoordinator? p25KeyRetrieval;
    internal ReceiveMuteState ReceiveMute => receiveOutputMutePolicy.State;
    private PatchGroupEditorViewModel? activeMultiSelectGroup;
    private readonly CallRecordingManager callRecordings;
    internal ConsoleOperationalRuntime OperationalRuntime => operationalRuntime;
    private readonly RecordingPlaybackCoordinator recordingPlayback;
    private readonly RecordingCommandController recordingCommands;
    internal RecordingPlaybackChannelState RecordingPlaybackState => operationalRuntime.RecordingPlaybackState;
    private readonly ConsoleSessionRuntime sessionRuntime;
    private readonly ConsoleSessionRuntime.ConsoleSessionTimer? audioMeterTimer;
    private readonly AudioRuntimeSettingsTransaction audioRuntimeSettings;
    private readonly BackgroundAppearanceController backgroundAppearance;
    private int disposeStarted;
    internal ConsoleSessionStatus SessionStatus { get; }
    private readonly CoalescedUiAction statusRefresh;
    private ConsoleSessionStatusSnapshot presentedStatus;
    private IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions> receiveAudioProcessingOptions =
        new Dictionary<VocoderMode, ReceiveAudioProcessingOptions>();
    private string clockText = string.Empty;
    private bool busy;
    private bool codeplugDiagnosticsDismissed;
    private readonly ChannelSelectionController channelSelection;
    private readonly ShellLayoutController shellLayout;

    internal MainWindowViewModel(
        string statusText,
        IEnumerable<SystemViewModel> systems,
        IEnumerable<ZoneViewModel> zones,
        MainWindowViewModelOptions? options = null)
        : this(PrepareShell(statusText, systems, zones, options))
    {
    }

    private MainWindowViewModel(ShellPreparation preparation)
    {
        ConsoleSessionServices ownership = preparation.Options.SessionServices!;
        SessionTerminalFence terminal = preparation.Options.PreparedState!.Terminal;
        try
        {
            string statusText = preparation.StatusText;
            SystemViewModel[] systems = preparation.Systems;
            ZoneViewModel[] zones = preparation.Zones;
            MainWindowViewModelOptions options = preparation.Options;
            PreparedLiveSession preparedLive = options.PreparedLiveSession
                ?? throw new InvalidOperationException("Prepare the operational runtime before constructing its shell.");
            preparedSession = preparedLive;
            pttStateChangeLock = preparedLive.PttStateChangeLock;
            transmitAdmissionGate = preparedLive.TransmitAdmissionGate;
            audioReconfigurationLock = preparedLive.AudioReconfigurationLock;
            receiveLifecycleSync = preparedLive.ReceiveSync;
            preparedLive.Attach(this);
            MainWindowSessionComposition composition = options.SessionComposition ??
                MainWindowSessionComposition.Default;
            IP25KeyResolver? p25KeyResolver = options.P25KeyResolver;
            UserSettingsStore? userSettingsStore = options.UserSettingsStore;
            IEnumerable<GroupConfiguration>? groupDefinitions = options.GroupDefinitions;
            Func<IReadOnlyList<string>>? serialPortProvider = options.SerialPortProvider;
            Func<string, int, IPttSource>? serialPttFactory = options.SerialPttFactory;
            IDmrKeyResolver? dmrKeyResolver = options.DmrKeyResolver;
            INxdnKeyResolver? nxdnKeyResolver = options.NxdnKeyResolver;
            string? codeplugPath = options.CodeplugPath;
            configurationReference = options.ConfigurationReference;
            PreparedTopology = options.PreparedTopology;
            PreparedState = options.PreparedState;
            receiveCallEpisodes = options.PreparedState?.ReceiveEpisodes ?? new();
            terminalFence = options.PreparedState?.Terminal ?? new();
            pttActivationArbiter = options.PreparedState?.ManualTransmitOwnership ?? new();
            receiveOutputMutePolicy = new ReceiveOutputMutePolicy(options.PreparedState?.ReceiveMute);
            IUiDispatcher? uiDispatcher = options.UiDispatcher;
            ConsoleSessionServices? sessionServices = options.SessionServices;
            bool networkDisabledDemo = options.NetworkDisabledDemo;
            bool migrateLegacyConfigurationOperatorState = options.MigrateLegacyConfigurationOperatorState;
            Func<ApplicationAudioConfiguration, Task>? reconfigureApplicationAudio = options.ReconfigureApplicationAudio;
            ConsoleSessionServices services = sessionServices ?? new ConsoleSessionServices();
            sessionRuntime = new ConsoleSessionRuntime(
                services,
                AvaloniaApplicationScheduler.Instance,
                faultHandler: ReportScheduledWorkFailure);
            var constructedSystems = new List<SystemViewModel>();
            foreach (SystemViewModel system in systems)
                constructedSystems.Add(system);
            Systems = constructedSystems.ToArray();
            Zones = zones.ToArray();
            channelsById = Systems
                .SelectMany(system => system.Channels)
                .Concat(Zones.SelectMany(zone => zone.Channels))
                .GroupBy(channel => new ChannelId(channel.SessionId))
                .ToDictionary(group => group.Key, group => group.First());
            operationalRuntime = preparedLive.Runtime;
            systemChannelIds = Systems.SelectMany(system => system.Channels).Select(channel => channel.Id).Distinct().ToArray();
            this.uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
            toneTargetRefresh = new CoalescedUiAction(this.uiDispatcher, () =>
            {
                if (!terminalFence.IsClosed)
                    RefreshToneTargets();
            });
            sessionUiCallbacks = new SessionUiCallbackGate(this.uiDispatcher);
            SessionStatus = PreparedState?.Status ?? new ConsoleSessionStatus();
            SessionStatus.SetConsole(statusText);
            presentedStatus = SessionStatus.Snapshot;
            statusRefresh = new CoalescedUiAction(this.uiDispatcher, PublishSessionStatus);
            SessionStatus.Changed += HandleSessionStatusChanged;
            services.Presentation.Register("session-status-presentation", () =>
            {
                SessionStatus.Changed -= HandleSessionStatusChanged;
                statusRefresh.Dispose();
                return ValueTask.CompletedTask;
            });
            codeplugDiagnosticsText = statusText;
            this.networkDisabledDemo = networkDisabledDemo;
            this.userSettingsStore = userSettingsStore ?? new UserSettingsStore(UserSettingsStore.DefaultPath);
            assetStore = options.AssetStore ?? new ManagedAssetStore(Path.Combine(
                Path.GetDirectoryName(this.userSettingsStore.Path) ?? AppContext.BaseDirectory,
                "Assets"));
            audioBackendFactory = options.AudioBackendFactory ?? new DesktopAudioBackendFactory(
                Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));
            vocoderFactory = options.VocoderFactory ?? new NativeVocoderFactory();
            privacyPermissionService = options.PrivacyPermissionService ??
                DesktopPrivacyPermissionService.Instance;
            userSettings = preparedLive.Settings;
            channelPreferences = new ConsoleChannelSettingsPersistence(
                systemChannelIds.Select(id => channelMedia.State(id)).ToArray(), userSettings, PersistUserSettings);
            channelSelection = new ChannelSelectionController(Systems, userSettings);
            loadedCodeplugPath = string.IsNullOrWhiteSpace(codeplugPath)
                ? string.Empty
                : Path.GetFullPath(codeplugPath);
            if (configurationReference is not null && loadedCodeplugPath.Length > 0)
            {
                ConfigurationOperatorStateStore.Activate(
                    userSettings,
                    configurationReference.Id.ToString(),
                    loadedCodeplugPath,
                    migrateLegacyConfigurationOperatorState);
            }
            verboseDiagnosticLogging = userSettings.VerboseLoggingEnabled ||
                VerboseDiagnosticLogging.IsEnabled;
            shellSettings = composition.CreateShellSettings(
                this.userSettingsStore,
                userSettings,
                new ShellSettingsSessionPort(
                    () => terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0,
                    CaptureConfigurationOperatorState,
                    () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NamedSettingsProfiles))),
                    text => StatusText = text,
                    exception =>
                    {
                        DesktopCrashLog.Write("User settings persistence", exception);
                        PostToUi(() => ReportUserSettingsPersistenceFailure(exception));
                    }), this.uiDispatcher);
            if (NormalizeHiddenAudioProcessingMode(userSettings))
                PersistUserSettings();
            audioBackendProvider = preparedLive.AudioBackendProvider;
            Func<ApplicationAudioConfiguration, Task> reconfigureAudio =
                reconfigureApplicationAudio ?? ReconfigureApplicationAudioAsync;
            debugLogs = new DebugLogWorkspace(
                this.uiDispatcher.CheckAccess,
                action => PostToUi(action, background: true),
                () => Volatile.Read(ref disposeStarted) != 0);
            debugLogs.PropertyChanged += HandleDebugLogWorkspacePropertyChanged;
            operatorUndo = composition.CreateOperatorUndo(
                action => PostToUi(action),
                exception => DesktopCrashLog.Write("Operator undo", exception));
            operatorUndo.Changed += HandleOperatorUndoChanged;
            backgroundAppearance = new BackgroundAppearanceController(
                assetStore,
                userSettings,
                this.uiDispatcher,
                PersistUserSettings);
            backgroundAppearance.Changed += HandleBackgroundAppearanceChanged;
            backgroundAppearance.StatusChanged += HandleBackgroundAppearanceStatusChanged;
            backgroundAppearance.Warning += HandleBackgroundAppearanceWarning;
            backgroundAppearance.BeginInitialLoad();
            foreach (SystemViewModel system in Systems)
                system.SetVerboseLogging(this.verboseDiagnosticLogging);
            this.serialPortProvider = serialPortProvider ?? SerialPttSource.GetAvailablePortNames;
            historyRecording = composition.CreateHistoryRecording(
                userSettings.RecordingRetentionDays.ToString(CultureInfo.InvariantCulture),
                GetDefaultRecordingRoot(userSettings.RecordingRootPath), PreparedState?.History);
            historyRecording.PropertyChanged += HandleHistoryRecordingPropertyChanged;
            historyDiagnostics = composition.CreateHistoryDiagnostics(
                historyRecording,
                debugLogs,
                new HistoryDiagnosticsSessionPort(
                    () => SelectedSystem,
                    this.uiDispatcher.CheckAccess,
                    action => PostToUi(action),
                    text => StatusText = text,
                    propertyName => PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(propertyName))), preparedLive.SubscriberCommands);
            audioSettings = new AudioSettingsViewModel(
                userSettings,
                ToAudioProcessingModeDisplay(userSettings.AudioProcessingMode));
            audioSettings.PropertyChanged += HandleAudioSettingsPropertyChanged;
            toneWorkspace = new ToneWorkspaceViewModel(userSettings, DesktopAlertToneFiles.Instance);
            toneWorkspace.PropertyChanged += HandleToneWorkspacePropertyChanged;
            tonePresentation = composition.CreateTonePresentation(
                toneWorkspace,
                userSettings,
                this);
            shellLayout = composition.CreateShellLayout(
                userSettings,
                Zones,
                new ShellLayoutSessionPort(
                    PersistUserSettings,
                    propertyName => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)),
                    text => StatusText = text));
            foreach (string path in userSettings.RecentCodeplugPaths.Take(UserSettings.MaximumRecentCodeplugs))
                recentCodeplugPaths.Add(path);
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
            pttSession = composition.CreatePttSession(
                pttSettings,
                (portName, baudRate) => serialPttFactory is null
                    ? new SerialPttInputSourceFactory(portName, baudRate)
                    : new DelegateSerialPttInputSourceFactory(
                        () => serialPttFactory(portName, baudRate)),
                GetSerialPttTargetScope);
            hardwarePttSequencer = new HardwarePttSequencer(pttStateChangeLock, this, pttSession, pttActivationArbiter);
            RefreshSerialPttDevices();
            if (SerialPttEnabled && SerialPttPortName.Length > 0)
            {
                SerialPttStatusText = $"Configured for {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
            }
            clockText = FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
            sessionRuntime.StartTimer(TimeSpan.FromSeconds(1), HandleClockTick);
            audioMeterTimer = null;
            sessionRuntime.StartTimer(TimeSpan.FromSeconds(1), HandleConnectionDiagnosticsTick);
            receiveBufferingRuntime.Apply(BuildReceiveJitterBufferSettingsBySystem());
            receiveAudioProcessingOptions = BuildReceiveAudioProcessingOptions();
            WebStreamPlaybackCoordinator webStreamPlayback = preparedLive.WebPlayback!;
            webStreamOperator = composition.CreateWebStreamOperator(
                userSettings,
                loadedCodeplugPath,
                webStreamPlayback,
                new WebStreamOperatorSessionPort(
                    () => networkDisabledDemo,
                    action => this.uiDispatcher.InvokeAsync(action),
                    PersistUserSettings,
                    text => AudioStatusText = text));
            RestoreToolbarClocks();
            p25KeyRing = p25KeyResolver as P25KeyRing;
            p25KeyRetrieval = preparedLive.KeyRetrieval;
            dmrKeyRing = dmrKeyResolver as DmrKeyRing;
            nxdnKeyRing = nxdnKeyResolver as NxdnKeyRing;
            callRecordings = preparedLive.Recordings;
            runtimeHealth = composition.CreateRuntimeHealth(callRecordings);
            callRecordings.RecordingFinalized += HandleRecordingFinalized;
            recordingPlayback = preparedLive.RecordingPlayback!;
            recordingCommands = composition.CreateRecordingCommands(this);
            receiveSystems = Systems.ToDictionary(system => system.Id, system => (system,
                preparedLive.IngressSystems[system.Id]));
            GroupConfiguration[] configuredGroups = (groupDefinitions ?? []).ToArray();
            transmitRuntime.ChannelInput = new ChannelTransmitInputPort(
                id => SelectChannel(ResolveChannel(id)),
                () => ObservePttActivationSource(PttActivationSource.LocalChannelControl),
                () => pttActivationArbiter.RecordStarted(PttActivationSource.LocalChannelControl),
                pttActivationArbiter.Clear);
            receiveSessionReconciler = preparedLive.ReceiveReconciler!;
            uiLatencyReporter = new UiLatencyReporter(PublishUiLatency);
            foreach (SystemViewModel system in Systems)
                RefreshJitterBufferTelemetry(system);
            RestoreChannelPresentation();
            audioRuntimeSettings = new AudioRuntimeSettingsTransaction(
                transmitCoordinator.SetKeepMicrophoneWarmAsync,
                reconfigureAudio);
            audioInputSettingsController = composition.CreateAudioInputSettings(
                audioSettings,
                userSettings,
                this);
            warmMicrophoneReconciler = new LatestBooleanStateReconciler(
                transmitCoordinator.SetKeepMicrophoneWarmAsync);
            warmMicrophoneReconciler.Reconciled += HandleWarmMicrophoneReconciled;
            if (userSettings.KeepTransmitMicrophoneWarm)
                _ = warmMicrophoneReconciler.SetDesired(true);
            patchRouting = composition.CreatePatchRouting(
                patchForwarding,
                Systems.SelectMany(system => system.Channels),
                configuredGroups,
                userSettings.RetainPatchStateOnStartup,
                new PatchRoutingSessionPort(
                    () => codeplugGroupState,
                    PersistUserSettings,
                    () => TaskObservation.Observe(SyncPatchSourceDecodeAsync()),
                    text => StatusText = text), preparedLive.PatchConfiguration);
            ToolbarClocks = new ReadOnlyObservableCollection<ToolbarClockViewModel>(toolbarClocks);
            RecentCodeplugPaths = new ReadOnlyObservableCollection<string>(recentCodeplugPaths);
            ConfigureWebStreams();
            RefreshRecordings(pruneExpired: userSettings.RecordingRetentionPolicyAccepted);
            ConfigureChannels();
            RefreshToneTargets();
            receiveRecordingTargets.Refresh();
            SubscribeToSystems();
            RestoreInitialSelection();
            RefreshActivityCallHistory();

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
            ApplyRecordingRetentionCommand = new RelayCommand(() =>
                AudioStatusText = "Confirm recording retention from the Recorder page before pruning.");
            RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
            defaultAudioDeviceMonitor = new DefaultAudioDeviceMonitor(
                new AudioBackendDeviceTopologyProvider(CreateReceiveAudioBackend),
                HandleAudioDeviceTopologyChangedAsync,
                (audioBackendFactory as IAudioDeviceChangeSourceFactory)?.CreateDeviceChangeSource());
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
            transmitCoordinator.ActiveChannelsChanged += HandleTransmitAvailabilityChanged;
            toneTransmitCoordinator.SendingChanged += HandleGeneratedAudioAvailabilityChanged;
            pttSession.StateChanged += HandlePttSourceStateChanged;
            pttSession.CaptureFailed += HandleGlobalPttCaptureFailed;
            pttSession.AttachEvents();
            if (this.userSettingsStore.LastLoadDiagnostics.Warning is { Length: > 0 } warning)
                StatusText = warning;
            var snapshots = operationalRuntime.GetOrCreateSnapshots(
                DesktopConsoleSnapshotProjector.BuildTopology(this), CreateSnapshotContextSource(), SessionStatus);
            operationalRuntime.ObserveSnapshotContext(ReceiveMute, RecordingPlaybackState, callRecordings, p25KeyRetrieval);
            preparedLive.CompleteAttachment();
        }
        catch (Exception failure)
        {
            terminal.TryClose();
            ConsoleSessionConstruction.RollbackAsync(failure, ownership.DisposeAsync)
                .AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => SessionStatus.Snapshot.Console;
        private set => SessionStatus.SetConsole(value);
    }

    public string CompactStatusText => SessionStatus.Snapshot.Latest;

    public string StatusDetailsText =>
        $"Console: {StatusText}{Environment.NewLine}" +
        $"Audio: {AudioStatusText}{Environment.NewLine}" +
        $"Transmit: {TransmitStatusText}";

    public bool IsCodeplugLoaded => Systems.Count > 0;

    public string? CurrentCodeplugPath => loadedCodeplugPath.Length == 0 ? null : loadedCodeplugPath;

    public ConfigurationReference? ConfigurationReference => configurationReference;
    internal ConsoleTopologySnapshot? PreparedTopology { get; }
    internal ConsoleSessionState? PreparedState { get; }
    internal P25KeyRetrievalCoordinator? KeyRetrieval => p25KeyRetrieval;

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

    internal async Task SaveMainWindowPlacementAsync(WindowPlacementSetting placement)
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
        await FlushUserSettingsAsync().ConfigureAwait(false);
    }

    public ReadOnlyObservableCollection<string> RecentCodeplugPaths { get; }

    public IReadOnlyList<string> NamedSettingsProfiles => shellSettings.NamedProfiles;

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
        get => shellLayout.ShowCallHistoryPane;
        set => shellLayout.ShowCallHistoryPane = value;
    }

    public bool IsActivitySidebarCollapsed => !ShowCallHistoryPane;

    public double ActivitySidebarWidth => ShowCallHistoryPane ? 250 : 34;

    public bool ShowSystemStatus
    {
        get => shellLayout.ShowSystemStatus;
        set => shellLayout.ShowSystemStatus = value;
    }

    public double UiFontSize
    {
        get => shellLayout.UiFontSize;
        set => shellLayout.UiFontSize = value;
    }

    public string UiFontSizeText => $"Text size: {UiFontSize:0}";
    public double UiSmallFontSize => UiFontSize - 2;
    public double UiCompactFontSize => UiFontSize - 3;
    public double UiHeadingFontSize => UiFontSize + 4;
    public double ChannelCardHeight => shellLayout.ChannelCardHeight;

    public double UiScale
    {
        get => shellLayout.UiScale;
        set => shellLayout.UiScale = value;
    }

    public string UiScaleText => $"Interface scale: {UiScale * 100:0}%";
    public ScaleTransform UiScaleTransform => shellLayout.ScaleTransform;

    public bool ShowChannels
    {
        get => shellLayout.ShowChannels;
        set => shellLayout.ShowChannels = value;
    }

    public bool ShowAlertTones
    {
        get => shellLayout.ShowAlertTones;
        set => shellLayout.ShowAlertTones = value;
    }

    public bool LockWidgets
    {
        get => shellLayout.LockWidgets;
        set => shellLayout.LockWidgets = value;
    }

    public IBrush MainBackgroundBrush => backgroundAppearance.Brush;

    public bool CanResizeLayout => !shellLayout.LockWidgets;

    public string? UserBackgroundImage => backgroundAppearance.Reference;

    public async Task<bool> SetUserBackgroundAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        return await backgroundAppearance.SetAsync(
            displayName,
            mediaType,
            content,
            cancellationToken).ConfigureAwait(false);
    }

    public void ClearUserBackground()
    {
        backgroundAppearance.Clear();
    }

    private ValueTask DeleteAssetIfUnreferencedAsync(string? removedAssetId)
        => backgroundAppearance.DeleteIfUnreferencedAsync(removedAssetId);

    public void ResetLayout()
    {
        ShellLayoutSnapshot snapshot = shellLayout.Capture();
        shellLayout.Reset();
        BeginUndoableAction(
            "Channel widget layout reset.",
            () =>
            {
                shellLayout.Restore(snapshot);
                return ValueTask.CompletedTask;
            });
    }

    public void MoveChannelWidget(ChannelViewModel channel, double x, double y, bool persist)
        => shellLayout.MoveChannelWidget(channel, x, y, persist);

    public void MoveWebStreamWidget(WebStreamViewModel stream, double x, double y, bool persist)
        => shellLayout.MoveWebStreamWidget(stream, x, y, persist);

    public void ExportSettings(string path)
        => shellSettings.Export(path);

    public void ExportSettings(Stream destination)
        => shellSettings.Export(destination);

    public SettingsImportPreview PreviewSettingsImport(string path)
        => shellSettings.PreviewImport(path);

    public SettingsImportStage StageSettingsImport(Stream source, string sourceName)
        => shellSettings.StageImport(source, sourceName);

    public SettingsImportPreview PreviewNamedSettingsProfile(string profileName)
        => shellSettings.PreviewNamedProfile(profileName);

    public SettingsImportStage StageNamedSettingsProfile(string profileName)
        => shellSettings.StageNamedProfile(profileName);

    public void ImportSettings(string path, SettingsImportScope scope = SettingsImportScope.All)
        => shellSettings.Import(path, scope);

    public void ImportSettings(Stream source, SettingsImportScope scope = SettingsImportScope.All)
        => shellSettings.Import(source, scope);

    public void ImportSettings(
        SettingsImportStage stage,
        SettingsImportScope scope = SettingsImportScope.All,
        bool acceptRecordingPolicy = false)
        => shellSettings.Import(stage, scope, acceptRecordingPolicy);

    public void SaveNamedSettingsProfile(string profileName)
        => shellSettings.SaveNamedProfile(profileName);

    public void ImportNamedSettingsProfile(
        string profileName,
        SettingsImportScope scope = SettingsImportScope.OperatorState)
        => shellSettings.ImportNamedProfile(profileName, scope);

    public void ImportNamedSettingsProfile(
        string profileName,
        SettingsImportStage stage,
        SettingsImportScope scope,
        bool acceptRecordingPolicy)
        => shellSettings.ImportNamedProfile(profileName, stage, scope, acceptRecordingPolicy);

    public void DeleteNamedSettingsProfile(string profileName)
        => shellSettings.DeleteNamedProfile(profileName);

    public void ResetSettings()
        => shellSettings.Reset();

    public void ClearCallHistory()
        => historyDiagnostics.ClearHistory();

    public void AddEventHistory(
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
        => historyDiagnostics.AddEvent(source, message, ridText, tgidText);

    public void ExportCallHistory(string path)
        => historyDiagnostics.ExportHistory(path);

    public void ExportCallHistory(Stream destination, bool leaveOpen = false)
        => historyDiagnostics.ExportHistory(destination, leaveOpen);

    public string AudioStatusText
    {
        get => SessionStatus.Snapshot.Audio;
        private set => SessionStatus.SetAudio(value);
    }

    public string TransmitStatusText
    {
        get => SessionStatus.Snapshot.Transmit;
        private set => SessionStatus.SetTransmit(value);
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
        get => ReceiveMute.GloballyMuted;
        set
        {
            if (ReceiveMute.GloballyMuted == value)
                return;

            audioCoordinator.SetOutputMuted(value);
            ReceiveMute.SetGlobalMuted(value);
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

    public bool IsMacOsPermissionRequestAvailable
        => privacyPermissionService.IsMacOsPermissionRequestAvailable;

    public void RequestMacOsKeyboardPermission()
    {
        try
        {
            KeyboardPermissionState result = privacyPermissionService.RequestKeyboardAccess();
            AudioStatusText = result switch
            {
                KeyboardPermissionState.Granted => "macOS keyboard access is already granted.",
                KeyboardPermissionState.Requested =>
                    "macOS keyboard access requested. Approve the prompt, or enable DVM Console under System Settings > Privacy & Security > Input Monitoring.",
                _ => "macOS keyboard access is unavailable on this platform."
            };
        }
        catch (Exception exception)
        {
            AudioStatusText = $"Unable to request macOS keyboard access: {exception.Message}";
        }
    }

    public async Task RequestMacOsMicrophonePermissionAsync()
    {
        try
        {
            MicrophonePermissionState result = await privacyPermissionService.RequestAsync();
            AudioStatusText = result switch
            {
                MicrophonePermissionState.Granted => "macOS microphone access is already granted.",
                MicrophonePermissionState.Requested =>
                    "macOS microphone access requested. Approve the system prompt to enable transmit audio.",
                MicrophonePermissionState.Denied =>
                    "macOS microphone access is denied. Enable DVM Console under System Settings > Privacy & Security > Microphone.",
                MicrophonePermissionState.Restricted =>
                    "macOS microphone access is restricted by system policy.",
                _ => "macOS microphone access is unavailable on this platform."
            };
        }
        catch (Exception exception)
        {
            AudioStatusText = $"Unable to request macOS microphone access: {exception.Message}";
        }
    }

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

    public bool LocalToneMonitorEnabled
    {
        get => userSettings.LocalToneMonitorEnabled;
        set
        {
            if (userSettings.LocalToneMonitorEnabled == value)
                return;
            userSettings.LocalToneMonitorEnabled = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalToneMonitorEnabled)));
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
            backgroundAppearance.ApplyTheme(value);
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

    internal KeyboardPttKey AppliedGlobalPttKey => pttSession.GlobalKey;

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

    internal KeyboardPttKey AppliedActiveSystemPttKey => pttSession.ActiveSystemKey;

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
        : SelectedChannel is null
        ? $"Choose TX on one or more cards. Global PTT: {GlobalPttKeyText}. Active-system PTT: {ActiveSystemPttKeyText}."
        : $"RX focus: {SelectedChannel.Name}. Global PTT: {GlobalPttKeyText}. Active-system PTT: {ActiveSystemPttKeyText}.";

    public IReadOnlyList<SystemViewModel> Systems { get; }
    public IReadOnlyList<KeyStatusItemViewModel> KeyStatusItems
        => Systems
            .SelectMany(system => system.Channels)
            .Where(channel => channel.Definition.IsEncrypted)
            .Select(channel => KeyStatusItemViewModel.From(channel, p25KeyRing, dmrKeyRing, nxdnKeyRing))
            .ToArray();
    public bool HasNoKeyStatusItems => KeyStatusItems.Count == 0;
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    internal event EventHandler? PatchStateChanged
    {
        add => patchRouting.ConfigurationStateChanged += value;
        remove => patchRouting.ConfigurationStateChanged -= value;
    }
    public IReadOnlyList<string> PatchGroupNames => patchRouting.GroupNames;
    public IReadOnlyList<PatchGroupEditorViewModel> PatchGroups => patchRouting.Groups;
    internal event Action<ChannelId> RecordingStateChanged
    {
        add => callRecordings.RecordingStateChanged += value;
        remove => callRecordings.RecordingStateChanged -= value;
    }
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
    public ReadOnlyObservableCollection<SubscriberCommandAuditEntry> SubscriberCommandAudit
        => historyDiagnostics.SubscriberCommandAudit;
    public ReadOnlyObservableCollection<DebugLogEntry> DebugLogEntries => debugLogs.Entries;
    public ReadOnlyObservableCollection<WebStreamViewModel> WebStreams => webStreamOperator.Streams;
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry> CallHistory
        => historyRecording.CallHistory;
    public ReadOnlyObservableCollection<CallHistoryEntry> ActivityCallHistory
        => historyRecording.ActivityCallHistory;
    internal event NotifyCollectionChangedEventHandler? ActivityCallHistoryChanging
    {
        add => historyRecording.ActivityCallHistoryChanging += value;
        remove => historyRecording.ActivityCallHistoryChanging -= value;
    }
    public string ActivityZoneFilterButtonText => historyRecording.ActivityZoneFilterButtonText;
    public string ActivityReceiveFilterButtonText => historyRecording.ActivityReceiveFilterButtonText;
    public IReadOnlyList<SubscriberCommandAuditEntry> ActivitySubscriberCommandAudit
        => historyDiagnostics.ActivitySubscriberCommandAudit;
    public ReadOnlyObservableCollection<CallHistoryEntry> FilteredCallHistory
        => historyRecording.FilteredCallHistory;
    System.Collections.IEnumerable ICallHistoryViewModel.FilteredCallHistory
        => FilteredCallHistory;
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
    internal event EventHandler<DebugLogEntry>? DebugLogPublished
    {
        add => debugLogs.EntryPublished += value;
        remove => debugLogs.EntryPublished -= value;
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

    public bool ApplyRecordingRoot(bool acceptRecordingPolicy = false)
    {
        if (!callRecordings.TrySetRootPath(RecordingRootPathText, out string errorMessage))
        {
            RecordingRootPathText = callRecordings.RootPath;
            AudioStatusText = $"TAR storage unchanged: {errorMessage}";
            return false;
        }

        userSettings.RecordingRootPath = callRecordings.RootPath;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordingUnavailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingAvailabilityWarning)));
        if (acceptRecordingPolicy)
            userSettings.RecordingRetentionPolicyAccepted = true;
        PersistUserSettings();
        RefreshRecordings(pruneExpired: userSettings.RecordingRetentionPolicyAccepted);
        RecordingRootPathText = callRecordings.RootPath;
        AudioStatusText = $"TAR recordings now use {callRecordings.RootPath}.";
        return true;
    }

    public void ExportDebugLogs(string path)
        => historyDiagnostics.ExportDebugLogs(path);

    internal void ExportDebugLogs(Stream destination, string destinationName)
        => historyDiagnostics.ExportDebugLogs(destination, destinationName);

    internal void ReportDebugLogExportFailure(string message)
        => historyDiagnostics.ReportDebugExportFailure(message);

    public IBrush ConnectionBrush => SelectedSystem?.IsConnected == true
        ? SolidBrushCache.Get("#00C86A")
        : SolidBrushCache.Get("#7B8794");

    public bool TrySendSubscriberCommand(
        SystemViewModel system,
        P25SubscriberCommand command,
        string? destinationText,
        out string message)
        => historyDiagnostics.TrySendSubscriberCommand(
            system,
            command,
            destinationText,
            out message);

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
        => recordingCommands.Open(metadata);

    public async Task PlayRecordingAsync(CallRecordingMetadata metadata)
        => await recordingCommands.PlayAsync(metadata).ConfigureAwait(false);

    public async Task PlayCallHistoryRecordingAsync(CallHistoryEntry entry)
        => await recordingCommands.PlayHistoryAsync(entry).ConfigureAwait(false);

    public async Task ToggleCallHistoryRecordingPlaybackAsync(CallHistoryEntry entry)
        => await recordingCommands.ToggleHistoryPlaybackAsync(entry).ConfigureAwait(false);

    public async Task StopRecordingPlaybackAsync()
        => await recordingCommands.StopAsync().ConfigureAwait(false);

    public async Task DeleteRecordingAsync(CallRecordingMetadata metadata)
        => await recordingCommands.DeleteAsync(metadata).ConfigureAwait(false);

    bool IRecordingCommandSession.TryGetRecordingPath(
        CallRecordingMetadata metadata,
        out string recordingPath)
        => callRecordings.TryGetRecordingPath(metadata, out recordingPath);

    Task IRecordingCommandSession.StartPlaybackAsync(
        RecordingId? recordingId,
        string recordingPath,
        RecordingCallIdentity identity)
        => recordingId is RecordingId id
            ? recordingPlayback.StartAsync(id, recordingPath, identity: identity)
            : recordingPlayback.StartAsync(recordingPath, identity: identity);

    Task IRecordingCommandSession.StopPlaybackAsync()
        => recordingPlayback.StopAsync();

    Task IRecordingCommandSession.StopPlaybackIfActiveAsync(
        RecordingId? recordingId,
        string recordingPath)
        => recordingId is RecordingId id
            ? recordingPlayback.StopIfPlayingAsync(id)
            : recordingPlayback.StopIfPlayingAsync(recordingPath);

    bool IRecordingCommandSession.DeleteRecording(CallRecordingMetadata metadata)
        => callRecordings.DeleteRecording(metadata);

    void IRecordingCommandSession.RevealRecording(string recordingPath)
        => RecordingFileLauncher.Reveal(recordingPath);

    void IRecordingCommandSession.PublishRecordingCommand(
        RecordingCommandNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        void Publish()
        {
            AudioStatusText = notification.Message;
            switch (notification.Kind)
            {
                case RecordingCommandNotificationKind.MissingRecording:
                case RecordingCommandNotificationKind.DeleteFailed:
                    RefreshRecordings();
                    break;
                case RecordingCommandNotificationKind.Deleted
                    when notification.Recording is CallRecordingMetadata metadata:
                    RecordRecordingCatalogMutation();
                    recordingEntries.Remove(metadata);
                    callHistory.RemoveRecording(metadata);
                    NotifyCallHistoryChanged();
                    break;
            }
        }

        if (uiDispatcher.CheckAccess())
            Publish();
        else
            PostToUi(Publish);
    }

    ValueTask IRecordingCommandSession.PublishRecordingCommandAsync(
        RecordingCommandNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return uiDispatcher.InvokeAsync(() =>
            ((IRecordingCommandSession)this).PublishRecordingCommand(notification));
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

    public ChannelViewModel? SelectedChannel => channelSelection.SelectedChannel;
    public bool HasSelectedZone => SelectedSystem?.SelectedZone is not null;

    public SystemViewModel? SelectedSystem
    {
        get => channelSelection.SelectedSystem;
        set
        {
            if (!channelSelection.SelectSystem(value))
                return;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedZone)));
            historyDiagnostics.NotifySelectedSystemChanged();
            NotifyConnectionPresentationChanged();
            NotifySelectedOutputMutePresentationChanged();
            (ToggleSelectedSystemOutputMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleSelectedZoneOutputMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            RaiseGeneratedAudioCanExecuteChanged();
        }
    }

    public void ToggleActivityZoneFilter()
        => historyDiagnostics.ToggleActivityZoneFilter();

    public void ToggleActivityReceiveFilter()
        => historyDiagnostics.ToggleActivityReceiveFilter();

    private void HandleActivityChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelViewModel.IsAudioEnabled) ||
            sender is not ChannelViewModel channel ||
            SelectedSystem?.Channels.Contains(channel) != true)
        {
            return;
        }

        if (uiDispatcher.CheckAccess())
            historyDiagnostics.RefreshActivityHistory();
        else
            PostToUi(historyDiagnostics.RefreshActivityHistory);
    }

    private void HandleSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemViewModel.SelectedZone) && ReferenceEquals(sender, SelectedSystem))
        {
            historyDiagnostics.RefreshActivityHistory();
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
        {
            AppendDuplicatePttBindingWarning();
            return;
        }
        if (result.SerialError is not null)
        {
            SerialPttStatusText = $"Serial PTT unavailable on {SerialPttPortName}: {result.SerialError.Message}";
            TransmitStatusText = $"PTT idle; serial source unavailable: {result.SerialError.Message}";
            AppendDuplicatePttBindingWarning();
            return;
        }
        SerialPttStatusText = $"Serial PTT ready on {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
        TransmitStatusText = $"PTT idle; serial source {SerialPttPortName} ready for {SerialPttScopeText}.";
        AppendDuplicatePttBindingWarning();
    }

    private void AppendDuplicatePttBindingWarning()
    {
        if (!pttSession.HasSuppressedDuplicateKeyboardBinding)
            return;
        TransmitStatusText +=
            " Duplicate keyboard assignments were found; global PTT has precedence and the active-system duplicate is disabled until corrected.";
    }

    public void SelectChannel(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channelSelection.SelectChannel(channel, AnyPttSourcePressed))
            return;
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedChannel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSystem)));
        RaiseGeneratedAudioCanExecuteChanged();
    }

    public void ToggleChannelTransmitSelection(ChannelViewModel channel)
        => SetChannelTransmitSelection(channel, !channel.IsTransmitSelected);

    internal void SetChannelTransmitSelection(ChannelViewModel channel, bool selected)
    {
        ArgumentNullException.ThrowIfNull(channel);
        // Desktop queues the preference write synchronously for binding commands.
        transmitControls.SetSelectedAsync(channel.Id, selected).AsTask().GetAwaiter().GetResult();
    }

    public void ToggleAllTransmitSelection()
    {
        var scope = (SelectedSystem?.Channels ?? Systems.SelectMany(system => system.Channels))
            .Select(channel => channel.Id);
        // The desktop adapter schedules one settings write and completes synchronously.
        var result = transmitControls.SetSelectionAsync(scope, selected: null,
            SaveTransmitSelectionPreferencesAsync).AsTask().GetAwaiter().GetResult();
        TransmitStatusText = result.Count == 0
            ? "No transmit-capable channels are available in the selected system."
            : result.Selected
                ? $"Selected {result.Count} transmit-capable channel(s) for global TX."
                : "Cleared global TX selection.";
    }

    public void ToggleChannelPageSelection(ChannelViewModel channel)
        => SetChannelPageSelection(channel, !channel.IsPageSelected);

    internal void SetChannelPageSelection(ChannelViewModel channel, bool selected)
    {
        ArgumentNullException.ThrowIfNull(channel);
        transmitControls.SetToneSelected(channel.Id, selected, ConsoleToneTargets.Page);
    }

    public void ToggleChannelAlertSelection(ChannelViewModel channel)
        => SetChannelAlertSelection(channel, !channel.IsAlertSelected);

    internal void SetChannelAlertSelection(ChannelViewModel channel, bool selected)
    {
        ArgumentNullException.ThrowIfNull(channel);
        transmitControls.SetToneSelected(channel.Id, selected, ConsoleToneTargets.Alert);
    }

    private void PresentChannelSelection(ChannelSelectionResult result)
    {
        string name = channelMedia.State(result.Channel).Runtime.Definition.Name;
        if (result.Kind == TransmitSelectionKind.Alert && result.Applied)
            RaiseGeneratedAudioCanExecuteChanged();
        TransmitStatusText = (result.Kind, result.Applied, result.Selected) switch
        {
            (TransmitSelectionKind.Transmit, false, _) => $"{name} cannot be selected for TX.",
            (TransmitSelectionKind.Transmit, true, true) => $"{name} selected for global TX.",
            (TransmitSelectionKind.Transmit, true, false) => $"{name} removed from global TX.",
            (TransmitSelectionKind.Page, false, _) => $"{name} cannot be selected for paging.",
            (TransmitSelectionKind.Page, true, true) => $"{name} armed for QCII paging.",
            (TransmitSelectionKind.Page, true, false) => $"{name} removed from QCII paging.",
            (TransmitSelectionKind.Alert, false, _) => $"{name} cannot be selected for alerts.",
            (TransmitSelectionKind.Alert, true, true) => $"{name} armed for DTMF and alert tones.",
            _ => $"{name} removed from alert-tone targeting."
        };
    }

    public async Task SetGlobalPttKeyAsync(KeyboardPttKey key)
    {
        KeyboardPttKey previousKey = pttSession.GlobalKey;
        SelectedGlobalPttKey = key;
        if (key != KeyboardPttKey.None && key == pttSession.ActiveSystemKey)
        {
            SelectedGlobalPttKey = pttSession.GlobalKey;
            TransmitStatusText = $"{key} is already assigned to active-system PTT.";
            return;
        }
        if (pttSession.GlobalKey == key)
            return;

        try
        {
            await ReplaceKeyboardPttBindingAsync(
                PttTargetScope.AllSelectedResources,
                key).ConfigureAwait(false);
        }
        catch
        {
            SelectedGlobalPttKey = previousKey;
            throw;
        }
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
        KeyboardPttKey previousKey = pttSession.ActiveSystemKey;
        SelectedActiveSystemPttKey = key;
        if (key != KeyboardPttKey.None && key == pttSession.GlobalKey)
        {
            SelectedActiveSystemPttKey = pttSession.ActiveSystemKey;
            TransmitStatusText = $"{key} is already assigned to global PTT.";
            return;
        }
        if (pttSession.ActiveSystemKey == key)
            return;

        try
        {
            await ReplaceKeyboardPttBindingAsync(
                PttTargetScope.ActiveSystem,
                key).ConfigureAwait(false);
        }
        catch
        {
            SelectedActiveSystemPttKey = previousKey;
            throw;
        }
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
                ChannelViewModel[] active = ResolveChannels(transmitCoordinator.ActiveChannels);
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

    private Task SetReceiveAsync(ReceiveSelectionScope scope, bool enabled)
        => receiveOutput.SetSelectionAsync(GetReceiveScopeChannels(scope).Select(channel => channel.Id).ToArray(),
            enabled, reportFailure: (id, failure) => ReportReceiveSelectionFailureAsync(ResolveChannel(id), failure));

    private Task ChangeChannelReceiveSelectionAsync(ChannelViewModel channel, bool? enabled)
        => receiveOutput.SetSelectionAsync([channel.Id], enabled,
            reportFailure: (id, failure) => ReportReceiveSelectionFailureAsync(ResolveChannel(id), failure));

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

    public async Task<bool> StartChannelTransmitAsync(ChannelViewModel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return await transmitRuntime.BeginChannelAsync(channel.Id,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task StopChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return transmitRuntime.EndChannelAsync(channel.Id);
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
        return transmitChannels.SelectManualChannels(
                systems.SelectMany(system => system.Channels).Select(channel => channel.Id))
            .Select(ResolveChannel).ToArray();
    }

    internal IReadOnlyList<ChannelViewModel> GetSerialPttTargets()
        => GetSelectedTransmitTargets(GetSerialPttTargetScope());

    internal bool PatchSourceIdPassthroughEnabled
        => patchForwarding.SourceIdPassthrough;

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
        bool networkDisabledDemo = false,
        ConfigurationReference? configurationReference = null,
        bool useLegacyPathFallback = true,
        bool migrateLegacyConfigurationOperatorState = false,
        Action<ConsoleSessionServiceDisposalTiming>? serviceDisposalObserved = null)
        => LoadAsync(configurationPath, userSettingsStore, serialPortProvider, serialPttFactory,
            uiDispatcher, networkDisabledDemo, configurationReference, useLegacyPathFallback,
            migrateLegacyConfigurationOperatorState, serviceDisposalObserved).GetAwaiter().GetResult();

    internal static async Task<MainWindowViewModel> LoadAsync(
        string? configurationPath,
        UserSettingsStore userSettingsStore,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null,
        IUiDispatcher? uiDispatcher = null,
        bool networkDisabledDemo = false,
        ConfigurationReference? configurationReference = null,
        bool useLegacyPathFallback = true,
        bool migrateLegacyConfigurationOperatorState = false,
        Action<ConsoleSessionServiceDisposalTiming>? serviceDisposalObserved = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userSettingsStore);
        cancellationToken.ThrowIfCancellationRequested();
        var dependencies = new DesktopRuntimeDependencies(
            userSettingsStore,
            serialPortProvider ?? SerialPttSource.GetAvailablePortNames,
            serialPttFactory ?? ((portName, baudRate) => new SerialPttSource(portName, baudRate)),
            uiDispatcher ?? AvaloniaUiDispatcher.Instance,
            new ManagedAssetStore(Path.Combine(
                Path.GetDirectoryName(userSettingsStore.Path) ?? AppContext.BaseDirectory,
                "Assets")),
            new DesktopAudioBackendFactory(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            new NativeVocoderFactory(),
            networkDisabledDemo,
            serviceDisposalObserved);
        return await new ConsoleSessionFactory(dependencies).CreateAsync(
            new ConsoleSessionLoader(userSettingsStore).Load(
                configurationPath,
                configurationReference,
                useLegacyPathFallback,
                migrateLegacyConfigurationOperatorState), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        operationalRuntime.Admission.Close();
        sessionUiCallbacks.Close();
        Interlocked.Exchange(ref disposeStarted, 1);
        return sessionRuntime.DisposeAsync();
    }

    public Task ToggleSystemConnectionAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!Systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));
        if (!networkDisabledDemo)
            return connectionSession.ToggleAsync(SystemId.FromName(system.Name));

        StatusText = "NEO deterministic demo · network connection requests are disabled.";
        TransmitStatusText = "Demo safety boundary: no FNE connection or outbound traffic was attempted.";
        return Task.CompletedTask;
    }

    internal void HandleSystemStatus(SystemViewModel system, FneConnectionStatus status)
        => preparedSession!.ApplyConnection(new(system.Id, system.Name,
            FneConnectionStateMapper.ToApplicationState(status.State), status.Message, status.ChangedAt));

    private void PresentSystemStatus(SystemViewModel system, FneConnectionStatus status, ConsoleConnectionChange transition,
        ConsoleCallHistoryRecord? preparedHistory = null)
    {
        void Apply()
        {
            if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0)
                return;

            system.ApplyStatus(status);
            NotifyConnectionPresentationChanged();
            bool stateChanged = transition.Changed;
            if (transition.LostConnection && p25KeyRing is not null)
                RefreshP25KeyState();
            if (stateChanged && status.State is FneConnectionState.Connected or FneConnectionState.Disconnected or FneConnectionState.Faulted)
            {
                if (preparedHistory is not null)
                {
                    callHistory.ProjectRuntimeRecord(preparedHistory);
                    NotifyCallHistoryChanged();
                }
                else
                {
                    string stateText = status.State.ToString().ToLowerInvariant();
                    AddEventHistory(
                        "FNE",
                        $"{system.Name} {stateText}",
                        system.SourceId?.ToString(CultureInfo.InvariantCulture),
                        system.Endpoint);
                }
            }
            bool shouldPlayChime = connectionChimeTracker.ShouldPlay(system.Name, FneConnectionStateMapper.ToApplicationState(status.State));
            if (stateChanged && shouldPlayChime)
                TaskObservation.Observe(PlayConnectionChimeAsync(system.Name, status.State));
            RaiseGeneratedAudioCanExecuteChanged();
        }

        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        if (uiDispatcher.CheckAccess())
            Apply();
        else
            PostToUi(Apply);
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
            PostToUi(() =>
                AudioStatusText = $"{systemName} connection chime unavailable: {exception.Message}");
        }
    }

    private bool SystemOwnsActiveTransmit(SystemViewModel system)
        => transmitChannels.OwnsActiveTransmit(system.Channels.Select(channel => channel.Id),
            transmitCoordinator.ActiveChannels);

    private Task StopTransmitForDisconnectedSystemAsync(SystemViewModel system, FneConnectionStatus status)
        => transmitRuntime.StopForDisconnectedSystemAsync(
            system.Channels.Select(channel => channel.Id).ToArray(), transmitChannels,
            pttStateChangeLock, transmitAdmissionGate, ClearDisconnectedTransmitLatches,
            pttActivationArbiter.Clear,
            $"Transmission stopped because {system.Name} is {status.State.ToString().ToLowerInvariant()}.");

    private void ClearDisconnectedTransmitLatches()
    {
        if (TogglePttMode && pttActivationArbiter.TryGetKeyboardOwner(
                out PttTargetScope scope, out PttActivationSource source))
            pttSession.ReleaseKeyboardToggleLatch(scope, source);
        pttSession.ReleaseAllKeyboardToggleLatches();
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
        => terminalFence.TryRun(() =>
            historyDiagnostics.AddDebugLog(timestamp, source, severity, message));

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

    private void HandleSystemJitterBufferChanged(object? sender, EventArgs e)
    {
        if (sender is SystemViewModel system)
            TaskObservation.Observe(ApplyRxJitterBufferAsync(system));
    }

    internal static IReadOnlyList<(byte AlgorithmId, ushort KeyId)> ResolveConfiguredP25KeyRequests(
        IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return P25ConfiguredKeyRequests.Resolve(channels.Select(channel => channel.Definition));
    }

    private void HandleSystemKeyResponse(object? sender, FneKeyResponse response)
    {
        if (sender is SystemViewModel system &&
            response.SystemName.Equals(system.Name, StringComparison.OrdinalIgnoreCase))
            preparedSession!.ApplyKey(new(system.Id, response.AlgorithmId, response.KeyId, response.KeyMaterial));
    }

    private void RefreshP25KeyState()
    {
        foreach (ChannelViewModel channel in Systems.SelectMany(candidate => candidate.Channels))
            channel.RefreshEncryptionState();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeyStatusItems)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoKeyStatusItems)));
    }

    private ValueTask SaveTransmitControlPreferenceAsync(ChannelId id,
        ChannelTransmitPreferenceChange change, CancellationToken token)
        => channelPreferences.SaveTransmitAsync(id, change, token);

    private ValueTask SaveTransmitSelectionPreferencesAsync(IReadOnlyList<ChannelId> channels,
        bool selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var changed = channels.ToHashSet();
        userSettings.TransmitSelectedChannelKeys = systemChannelIds
            .Where(id => changed.Contains(id) ? selected : channelMedia.State(id).Operator.Snapshot.TransmitSelected)
            .Select(id => ResolveChannel(id).SettingsKey).ToList();
        PersistUserSettings();
        return ValueTask.CompletedTask;
    }

    internal async ValueTask SetChannelTransmitEncryptedAsync(ChannelId id, bool encrypted, CancellationToken token = default)
    {
        if (IsSessionInputSuppressed) return;
        await transmitControls.SetEncryptedAsync(id, encrypted, token).ConfigureAwait(false);
    }

    private void HandleChannelEncryptionChanged(object? sender, bool encrypted)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.TransmitEncryptionStates[channel.SettingsKey] = encrypted;
        PersistUserSettings();
    }

    private ValueTask SaveRecordingControlPreferenceAsync(ChannelId id, bool enabled, CancellationToken token)
        => channelPreferences.SaveRecordingAsync(id, enabled, token);

    internal bool CanRecordChannel(ChannelId id) => recordingControls.CanRecord(id);

    internal async ValueTask SetChannelRecordingEnabledAsync(ChannelId id, bool enabled, CancellationToken token = default)
    {
        if (IsSessionInputSuppressed) return;
        await recordingControls.SetEnabledAsync(id, enabled, token).ConfigureAwait(false);
    }

    private Task ReconcileRecordingControlAsync(ChannelId id, bool enabled, CancellationToken token)
    {
        if (!enabled)
        {
            callRecordings.StopChannel(channelMedia.DescribeRecording(id));
            return StopRecordingDecodeIfUnusedAsync(ResolveChannel(id));
        }
        receiveAudioWork.Start(id);
        return receiveRuntime.EnsureRecordingAudioAsync(id, token);
    }

    private void HandleChannelRecordingChanged(object? sender, bool enabled)
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0 ||
            sender is not ChannelViewModel channel)
            return;

        receiveRecordingTargets.Refresh();
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

    internal Task SetChannelGainAsync(ChannelId id, double gain, CancellationToken cancellationToken)
        => channelAudioSettings.SetGainAsync(id, gain, cancellationToken);

    internal Task SetChannelBalanceAsync(ChannelId id, double balance, CancellationToken cancellationToken)
        => channelAudioSettings.SetBalanceAsync(id, balance, cancellationToken);

    private void HandleChannelVolumeChanged(object? sender, double volume)
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0 ||
            sender is not ChannelViewModel channel)
            return;

        TaskObservation.Observe(channelAudioSettings.SetGainAsync(channel.Id, volume));
    }

    private void HandleChannelStereoBalanceChanged(object? sender, double balance)
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0 ||
            sender is not ChannelViewModel channel)
            return;

        TaskObservation.Observe(channelAudioSettings.SetBalanceAsync(channel.Id, balance));
    }

    private ValueTask SaveChannelAudioPreferenceAsync(ChannelId id,
        ChannelReceivePreferenceChange change, CancellationToken cancellationToken)
        => channelPreferences.SaveAudioAsync(id, change, cancellationToken);

    internal Task RestoreSelectedWebStreamsForSessionAsync()
        => webStreamOperator.RestoreSelectedForSessionAsync();

    public bool SaveWebStreamOutputDevice(WebStreamViewModel stream)
        => webStreamOperator.SaveOutputDevice(stream);

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

    private IAudioBackend CreateReceiveAudioBackend()
        => audioBackendProvider.CreateBackend();

    private IVocoderBackend CreateReceiveVocoderBackend()
        => vocoderFactory.Create(Volatile.Read(ref receiveAudioProcessingOptions));

    // ProcessedAudioCapture confines DVM Console gain/EQ/AGC to microphone samples.
    private IAudioBackend CreateTransmitAudioBackend()
        => audioBackendProvider.CreateBackend();

    private IAudioBackend CreateNativeAudioBackend(ApplicationAudioConfiguration configuration)
        => audioBackendFactory.Create(new AudioBackendConfiguration(
            configuration.ProcessingMode,
            configuration.InputDeviceId,
            configuration.OutputDeviceId));

    private ApplicationAudioConfiguration CreateApplicationAudioConfiguration()
        => new(
            GetConfiguredAudioProcessingMode(),
            userSettings.AudioInputDeviceId,
            userSettings.AudioOutputDeviceId);

    private void HandleWarmMicrophoneReconciled(object? sender, LatestBooleanStateResult result)
    {
        PostToUi(() =>
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
        => DesktopSessionAudioPolicy.ResolveProcessingMode(userSettings.AudioProcessingMode, OperatingSystem.IsWindows());

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

    private string? GetChannelOutputDeviceId(ChannelId channel)
    {
        var definition = channelMedia.State(channel).Runtime.Definition;
        string settingsKey = $"{definition.SystemName}\u001F{definition.Name}";
        if (userSettings.ChannelOutputDeviceIds.TryGetValue(settingsKey, out string? channelDeviceId))
            return channelDeviceId;
        return userSettings.AudioOutputDeviceId;
    }

    private Task EnsureRecordingAudioAsync(ChannelViewModel channel, CancellationToken cancellationToken = default)
        => receiveRuntime.EnsureRecordingAudioAsync(channel.Id, cancellationToken);

    private Task ReportRecordingDecodeFailureAsync(ChannelId channelId, Exception exception)
        => RunOnUiThreadAsync(() =>
        {
            string name = ResolveChannel(channelId).Name;
            AddDebugLog(DateTimeOffset.Now, "TAR", DebugLogSeverity.Error,
                $"TAR decode unavailable on {name}; selection retained, retrying. {exception}");
            AudioStatusText = $"TAR decode unavailable for {name}; retrying: {exception.Message}";
        });

    private async Task StopRecordingDecodeIfUnusedAsync(ChannelViewModel channel)
    {
        if (channel.IsAudioEnabled || !audioCoordinator.IsActive(channel))
            return;

        try
        {
            await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
            receiveDiagnostics.ResetJitter(channel);
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
        ChannelId channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        // An enabled patch owns a dedicated jitter-buffered decoder for its
        // source. Do not feed the same PCM a second time from Listen or TAR.
        if (!patchSourceDecode.IsActive(channel))
            patchForwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples);
        recordingRuntime.ObserveDecoded(channel, streamId, sourceId, samples);
        LogVocoderAudioLevel(channel, samples.Span, ChannelAudioDirection.Receive, streamId);
    }

    private void HandlePresentedReceiveSamples(
        ChannelId channelId,
        uint streamId,
        ReadOnlyMemory<short> samples,
        TimeSpan presentationDelay)
    {
        ObserveAudioMeter(channelId, streamId, samples.Span, ChannelAudioDirection.Receive, presentationDelay);
    }

    private ChannelViewModel ResolveChannel(ChannelId channelId)
        => channelsById.TryGetValue(channelId, out ChannelViewModel? channel)
            ? channel
            : throw new KeyNotFoundException($"Channel session '{channelId}' is not part of this console session.");

    private ChannelViewModel[] ResolveChannels(IEnumerable<ChannelId> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        return channelIds
            .Distinct()
            .Select(ResolveChannel)
            .ToArray();
    }

    private void HandleTransmitSamples(
        ChannelId channel,
        uint streamId,
        uint sourceId,
        ReadOnlySpan<short> samples)
    {
        transmitState.ObserveSamples(channel, streamId, sourceId, samples);
        ObserveAudioMeter(channel, streamId, samples, ChannelAudioDirection.Transmit);
        LogVocoderAudioLevel(channel, samples, ChannelAudioDirection.Transmit, streamId);
    }

    private void LogVocoderAudioLevel(
        ChannelId channel,
        ReadOnlySpan<short> samples,
        ChannelAudioDirection direction,
        uint streamId = 0)
    {
        if (!verboseDiagnosticLogging || samples.IsEmpty)
            return;

        IReadOnlyList<PcmLevelMeasurement> measurements = audioLevelLogs.Observe(channel, direction, streamId, samples);
        if (measurements.Count == 0) return;
        var definition = channelMedia.State(channel).Runtime.Definition;
        DateTimeOffset now = DateTimeOffset.Now;
        string streamText = streamId == 0 ? string.Empty : $", stream {streamId}";
        foreach (PcmLevelMeasurement measurement in measurements)
        {
            AddDebugLog(
                now,
                definition.SystemName,
                DebugLogSeverity.Debug,
                $"Vocoder {direction.ToString().ToUpperInvariant()} {ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol).ToString().ToUpperInvariant()} " +
                $"on {definition.Name}: PCM RMS {measurement.RmsDbfs:0.0} dBFS, " +
                $"peak {measurement.PeakDbfs:0.0} dBFS over " +
                $"{FormatAudioLevelDuration(measurement.SampleCount)}{streamText}.");
        }
    }

    internal static string FormatAudioLevelDuration(long sampleCount)
        => TransmitAudioTransitionController.FormatAudioLevelDuration(sampleCount);

    private void HandleAudioMeterTick(object? sender, EventArgs e)
    {
        if (preparedSession is { } prepared) { prepared.AdvanceMeters(); return; }
        lock (audioMeterSync)
        {
            if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0) return;
            audioMeterPipeline.Advance();
            if (!audioMeterPipeline.HasActivity) audioMeterTimer?.Stop();
        }
    }

    private void ObserveAudioMeter(ChannelId channel, uint stream, ReadOnlySpan<short> samples,
        ChannelAudioDirection direction, TimeSpan presentationDelay = default)
    {
        if (preparedSession is { } prepared)
        {
            prepared.ObserveMeter(channel, stream, samples, direction, presentationDelay);
            return;
        }
        // Observe/start and the final tick/stop share admission, so a new PCM
        // window cannot be stranded by a simultaneous transition to idle.
        lock (audioMeterSync)
        {
            if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0) return;
            if (audioMeterPipeline.Observe(channel, stream, samples, direction, presentationDelay))
                audioMeterTimer?.Start();
        }
    }

    private async Task SyncPatchSourceDecodeAsync(CancellationToken cancellationToken = default)
    {
        if (preparedSession is { } prepared)
        {
            await prepared.SynchronizePatchSourcesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await patchSourceDecode
                .ApplyChannelsAsync(
                    GetActivePatchSourceChannels(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PostToUi(() =>
                AudioStatusText = $"Patch source decode unavailable: {exception.Message}");
        }
    }

    internal ReceiveChannelDescriptor[] GetActivePatchSourceChannels()
        => PatchSourceSelectionPolicy.SelectEnabledSources(patchRouting.SavedGroups)
            .Select(channelMedia.DescribeReceive).ToArray();

    private void HandleRecordingFaulted(ChannelViewModel channel, Exception exception)
    {
        sessionUiCallbacks.Post(() => PublishRecordingFault(channel, exception));
    }

    internal void PublishRecordingFault(ChannelViewModel channel, Exception exception)
    {
        // The recording queue owns failed-file cleanup and subsequent attempts.
        // A delayed finalization failure must never disarm future calls.
        AddDebugLog(DateTimeOffset.Now, "TAR", DebugLogSeverity.Error,
            $"Recording failed on {channel.Name}; TAR selection retained. {exception}");
        AudioStatusText = $"TAR recording failed on {channel.Name}; selection retained: {exception.Message}";
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        sessionUiCallbacks.Post(() =>
        {
            if (result.Metadata is CallRecordingMetadata metadata && metadata.IsPlayable)
            {
                RecordRecordingCatalogMutation();
                CallRecordingMetadata? existing = recordingEntries.FirstOrDefault(candidate =>
                    RecordingDisplayIdentity.Matches(candidate, metadata));
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
        sessionUiCallbacks.Post(() =>
            AudioStatusText = $"Recording playback stopped: {exception.Message}");
    }

    private void HandleRecordingPlaybackStarted(RecordingPlaybackStartupMetrics metrics)
    {
        sessionUiCallbacks.Post(() => AddDebugLog(
            DateTimeOffset.Now,
            "Playback",
            metrics.FirstOutput >= TimeSpan.FromMilliseconds(500)
                ? DebugLogSeverity.Warning
                : DebugLogSeverity.Debug,
            $"Recording {metrics.RecordingId} first output in {metrics.FirstOutput.TotalMilliseconds:F0} ms " +
            $"(source {metrics.SourceOpen.TotalMilliseconds:F0}, " +
            $"decoder {(metrics.DecoderOpen - metrics.SourceOpen).TotalMilliseconds:F0}, " +
            $"prefetch {(metrics.FirstDecode - metrics.DecoderOpen).TotalMilliseconds:F0}, " +
            $"output {(metrics.OutputOpen - metrics.FirstDecode).TotalMilliseconds:F0}, " +
            $"notification {metrics.NotificationDuration.TotalMilliseconds:F0}, " +
            $"after notification {(metrics.FirstOutput - metrics.NotificationCompleted).TotalMilliseconds:F0}, " +
            $"pacing wait {metrics.FirstWritePacingWait.TotalMilliseconds:F0}, " +
            $"first audio write {metrics.FirstWriteDuration.TotalMilliseconds:F0}; " +
            "first output means write completion, not audible onset)."));
    }

    private void HandleRecordingPlaybackStateChanged(
        object? sender,
        RecordingPlaybackStateChangedEventArgs e)
    {
        ChannelId? channel = e.IsPlaying && e.Identity is { } identity
            ? RecordingChannelResolver.Find(systemChannelIds.Select(id => channelMedia.State(id).Runtime.Definition), identity)
            : null;
        var playback = RecordingPlaybackState.Apply(e.RecordingId, e.IsPlaying, channel);
        PresentRecordingPlaybackStateChanged(e, playback);
    }

    private void PresentRecordingPlaybackStateChanged(
        RecordingPlaybackStateChangedEventArgs e, RecordingPlaybackChannelSnapshot playback)
    {
        if (!e.IsPlaying && playback.Recording is not null) return;
        long queuedAt = Stopwatch.GetTimestamp();
        sessionUiCallbacks.Post(() =>
        {
            // A queued start/stop must not overwrite a newer playback transition.
            if (RecordingPlaybackState.Snapshot.Revision != playback.Revision) return;
            long applyingAt = Stopwatch.GetTimestamp();
            if (e.IsPlaying && e.Identity is null)
                RecordingPlaybackState.ResolveChannel(playback.Revision, ResolveRecordingPlaybackChannel(e));
            foreach (CallHistoryEntry entry in callHistory.Entries)
            {
                entry.SetRecordingPlaying(e.IsPlaying &&
                    RecordingDisplayIdentity.Matches(entry.Recording, e.RecordingId, e.Path));
            }
            uiLatencyReporter.Observe(
                e.IsPlaying ? UiLatencyCategory.RecordingPlay : UiLatencyCategory.RecordingStop,
                Stopwatch.GetElapsedTime(queuedAt, applyingAt),
                Stopwatch.GetElapsedTime(applyingAt),
                e.RecordingId.ToString());
        });
    }

    private ChannelId? ResolveRecordingPlaybackChannel(RecordingPlaybackStateChangedEventArgs playback)
    {
        IEnumerable<DvmConsole.Core.Runtime.ChannelRuntimeDefinition> channels = Systems.SelectMany(system => system.Channels)
            .Select(channel => channel.Definition);
        if (playback.Identity is { } identity) return RecordingChannelResolver.Find(channels, identity);
        CallRecordingMetadata? recording = recordingEntries.FirstOrDefault(candidate =>
            RecordingDisplayIdentity.Matches(candidate, playback.RecordingId, playback.Path));
        recording ??= callHistory.Entries
            .Select(entry => entry.Recording)
            .FirstOrDefault(candidate =>
                RecordingDisplayIdentity.Matches(candidate, playback.RecordingId, playback.Path));
        if (recording is null)
            return null;

        return RecordingChannelResolver.Find(channels, recording.ToCallIdentity());
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

    internal static bool TryParseRecordingRetentionDays(string? text, out int days)
    {
        return int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out days) &&
            days is >= 0 and <= 3650;
    }

    internal async Task<RecordingPolicyImpact> PreviewRecordingPolicyAsync(
        string? requestedRoot,
        int days,
        CancellationToken cancellationToken = default)
    {
        if (days is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days));
        string root = GetDefaultRecordingRoot(requestedRoot);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordingCatalogScanResult scan = await CallRecordingManager.PreviewRecordingPolicyAsync(
            root,
            days,
            now,
            cancellationToken).ConfigureAwait(false);
        return new RecordingPolicyImpact(
            root,
            days,
            days == 0 ? null : now.AddDays(-days),
            scan.RetentionCandidates);
    }

    internal void ApplyRecordingRetention(int days, bool acceptRecordingPolicy)
    {
        if (days is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days));

        userSettings.RecordingRetentionDays = days;
        if (acceptRecordingPolicy)
            userSettings.RecordingRetentionPolicyAccepted = true;
        callRecordings.RetentionDays = days;
        PersistUserSettings();
        RefreshRecordings(pruneExpired: userSettings.RecordingRetentionPolicyAccepted);
        RecordingRetentionDaysText = days.ToString(CultureInfo.InvariantCulture);
        AudioStatusText = days == 0
            ? "TAR retention pruning disabled."
            : $"TAR retention set to {days} day(s).";
    }


    private void HandlePttSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        PropertyChanged?.Invoke(this, args);
        if (args.PropertyName == nameof(SelectedGlobalPttKey))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedGlobalPttKeyName)));
        }
        else if (args.PropertyName == nameof(SelectedActiveSystemPttKey))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedActiveSystemPttKeyName)));
        }
    }

    private void HandleHistoryRecordingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        PropertyChanged?.Invoke(this, args);
        if (args.PropertyName == nameof(RecordingRootPathText))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RecordingLocationText)));
        }
    }

    private void HandleAudioSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandleToneWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
        => PropertyChanged?.Invoke(this, args);

    private void HandlePttSourceStateChanged(object? sender, PttSourceStateChange change)
        => DispatchKeyboardPttStateChanged(change.Pressed, change.Scope, change.Source);

    private void HandleGeneratedAudioAvailabilityChanged(object? sender, EventArgs args)
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0)
            return;
        if (uiDispatcher.CheckAccess())
            RaiseGeneratedAudioCanExecuteChanged();
        else
            PostToUi(RaiseGeneratedAudioCanExecuteChanged);
    }

    private void HandleTransmitAvailabilityChanged(
        object? sender,
        ActiveChannelsChangedEventArgs args)
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0)
            return;

        void RaiseCommandStates()
        {
            (ApplyAudioInputSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            RaiseGeneratedAudioCanExecuteChanged();
        }

        if (uiDispatcher.CheckAccess())
            RaiseCommandStates();
        else
            PostToUi(RaiseCommandStates);
    }

    private void DispatchKeyboardPttStateChanged(
        bool pressed,
        PttTargetScope scope,
        PttActivationSource source)
    {
        if (uiDispatcher.CheckAccess())
            TaskObservation.Observe(HandleKeyboardPttStateChangedAsync(pressed, scope, source));
        else
            PostToUi(
                () => TaskObservation.Observe(HandleKeyboardPttStateChangedAsync(pressed, scope, source)));
    }

    private Task HandleKeyboardPttStateChangedAsync(
        bool pressed,
        PttTargetScope scope,
        PttActivationSource source)
        => hardwarePttSequencer.HandleAsync(pressed, scope, source);

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
        if (!terminalFence.IsClosed)
            TaskObservation.Observe(Task.Run(() => HandleTransmitFaultedAsync(exception)));
    }

    private async Task HandleTransmitFaultedAsync(Exception exception)
    {
        if (terminalFence.IsClosed)
            return;
        await pttStateChangeLock.WaitAsync().ConfigureAwait(false);
        await transmitAdmissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (terminalFence.IsClosed)
                return;
            ObserveTransmitHealthError(exception);
            ChannelViewModel[] channels = ResolveChannels(transmitCoordinator.ActiveChannels)
                .Concat(Systems.SelectMany(system => system.Channels).Where(channel => channel.IsTransmitting))
                .Distinct()
                .ToArray();
            (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
                .Select(channel => (
                    channel,
                    transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId))))
                .Where(entry => entry.Item2 != 0)
                .ToArray();
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
                HashSet<ChannelId> unresolved = transmitCoordinator.ActiveChannels.ToHashSet();
                pttSession.ReleaseAllKeyboardToggleLatches();
                pttActivationArbiter.Clear();
                foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
                {
                    if (unresolved.Contains(new ChannelId(channel.SessionId)))
                        continue;
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
                    PostToUi(NotifyCallHistoryChanged);
                PostToUi(() =>
                {
                    foreach (ChannelViewModel channel in channels)
                    {
                        if (unresolved.Contains(new ChannelId(channel.SessionId)))
                            continue;
                        channel.SetTransmitEnabled(false);
                    }
                    if (unresolved.Count == 0)
                    {
                        activeMultiSelectGroup?.SetPttActive(false);
                        activeMultiSelectGroup = null;
                        TransmitStatusText = $"Transmission stopped: {exception.Message}";
                    }
                    else
                    {
                        TransmitStatusText =
                            $"Transmission faulted and release remains unconfirmed; retry unkey or disconnect: " +
                            $"{exception.Message}";
                    }
                });
            }
            if (transmitCoordinator.ActiveChannels.Count == 0)
            {
                try
                {
                    await RestoreSuspendedAudioAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    PostToUi(() =>
                        TransmitStatusText = $"Transmission stopped; audio recovery failed: {cleanupException.Message}");
                }
            }
        }
        finally
        {
            transmitAdmissionGate.Release();
            pttStateChangeLock.Release();
        }
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyAudioInputSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyRxAudioProcessingOptionsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void HandleSessionStatusChanged(object? sender, EventArgs args)
    {
        if (uiDispatcher.CheckAccess()) PublishSessionStatus();
        else statusRefresh.Schedule();
    }

    private void PublishSessionStatus()
    {
        if (terminalFence.IsClosed || Volatile.Read(ref disposeStarted) != 0) return;
        ConsoleSessionStatusSnapshot current = SessionStatus.Snapshot;
        ConsoleSessionStatusSnapshot previous = presentedStatus;
        if (ReferenceEquals(current, previous)) return;
        presentedStatus = current;
        if (current.Console != previous.Console)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        if (current.Audio != previous.Audio)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioStatusText)));
        if (current.Transmit != previous.Transmit)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransmitStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDetailsText)));
    }

    internal Task PrepareBackgroundAssetAsync(CancellationToken cancellationToken)
        => backgroundAppearance.PrepareAsync(cancellationToken);

    internal static string GetImageMediaType(string path)
        => BackgroundAppearanceController.GetImageMediaType(path);

    private void HandleBackgroundAppearanceChanged(object? sender, EventArgs args)
    {
        if (terminalFence.IsClosed)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserBackgroundImage)));
    }

    private void HandleBackgroundAppearanceStatusChanged(object? sender, string text)
    {
        if (!terminalFence.IsClosed)
            StatusText = text;
    }

    private void HandleBackgroundAppearanceWarning(object? sender, string text)
    {
        if (!terminalFence.IsClosed)
            AddDebugLog(DateTimeOffset.Now, "Assets", DebugLogSeverity.Warning, text);
    }

    private void RestoreChannelWidgetLayout()
        => shellLayout.RestoreChannelWidgetLayout();

    private void HandleClockTick(object? sender, EventArgs e)
    {
        if (preparedSession is null) historyDiagnostics.ExpireSubscriberCommands();
        RefreshClock();
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
        if (preparedSession is { } prepared) { prepared.AdvanceMaintenance(now); return; }
        lock (receiveLifecycleSync)
        {
            ExpireStaleReceiveRoutes(now);
            if (receiveEpisodeRetirement.Advance(now)) PostToUi(NotifyCallHistoryChanged);
        }
    }

    private void ReportReceiveEpisodeCompletionFailure(
        ReceiveCallEpisodeSnapshot episode,
        Exception exception)
    {
        if (exception is ObjectDisposedException && Volatile.Read(ref disposeStarted) != 0)
            return;

        DesktopCrashLog.Write("Receive episode completion", exception);
        TaskObservation.Observe(
            RunOnUiThreadAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Warning,
                $"Unable to complete receive episode {episode.EpisodeId}: {exception}")),
            reportingException => DesktopCrashLog.Write(
                "Receive episode completion UI reporting",
                reportingException));
    }

    private void NotifyCallHistoryChanged()
        => historyDiagnostics.RefreshHistory();

    private void RefreshActivityCallHistory()
        => historyDiagnostics.RefreshActivityHistory();

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
        if (Avalonia.Application.Current is not Avalonia.Application application)
            return;

        application.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void PersistUserSettings()
        => shellSettings.Schedule();

    private void CaptureConfigurationOperatorState()
    {
        if (configurationReference is not null && loadedCodeplugPath.Length > 0)
        {
            ConfigurationOperatorStateStore.CaptureActive(
                userSettings,
                configurationReference.Id.ToString(),
                loadedCodeplugPath);
        }
    }

    internal Task FlushUserSettingsAsync()
        => shellSettings.FlushAsync();

    internal Task AdoptStudioUserSettingsAsync(ConfigurationSavePlan plan)
        => shellSettings.AdoptStudioSnapshotAsync(plan);

    internal Task AdoptUserSettingsSnapshotAsync(UserSettingsSnapshot snapshot)
        => shellSettings.AdoptSnapshotAsync(snapshot);

    internal void ReportUserSettingsPersistenceFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StatusText = $"Operator settings could not be saved: {exception.Message}";
    }

    internal void ReportUnhandledUiFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StatusText = $"An application action could not be completed: {exception.Message}";
    }

    internal void ReportSessionReplacementFollowUpFailure(
        SessionReplacementFollowUpPhase phase)
    {
        StatusText = phase switch
        {
            SessionReplacementFollowUpPhase.RetiredSessionCleanup =>
                "Configuration opened, but the previous session did not close cleanly. " +
                "Restart DVM Console before transmitting if any old connection still appears active.",
            SessionReplacementFollowUpPhase.SelectedWebStreamRestore =>
                "Configuration opened, but one or more selected web streams could not be restored. " +
                "They can be started again from Audio settings.",
            _ => "Configuration opened, but a follow-up operation did not complete."
        };
    }

    private void ReportScheduledWorkFailure(Exception exception)
    {
        DesktopCrashLog.Write("Scheduled application work", exception);
        if (Volatile.Read(ref disposeStarted) == 0)
            ReportUnhandledUiFailure(exception);
    }

    public void ApplyPatchGroup(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
        => patchRouting.ApplyGroup(groupName, members, enabled, oneWay);

    public void ApplyPatchGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (ApplyGroupOperatorStates([group]) is { } error)
            StatusText = error;
    }

    public string? ApplyGroupOperatorStates(IEnumerable<PatchGroupEditorViewModel> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        PatchGroupEditorViewModel[] distinctGroups = groups.Distinct().ToArray();
        foreach (PatchGroupEditorViewModel group in distinctGroups)
        {
            if (group.IsMultiSelect && ReferenceEquals(activeMultiSelectGroup, group))
                return $"Stop multi-select PTT for '{group.Name}' before changing its membership.";
        }
        return patchRouting.ApplyOperatorStates(distinctGroups);
    }

    public void SetPatchGroupEnabled(PatchGroupEditorViewModel group)
        => patchRouting.SetEnabled(group);

    public async Task ToggleMultiSelectPttAsync(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.IsMultiSelect)
            return;

        if (group.IsPttActive)
        {
            ChannelViewModel[] active = ResolveChannels(transmitCoordinator.ActiveChannels);
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
            .OfType<ChannelViewModel>()
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
            pttActivationArbiter.RecordStarted(PttActivationSource.LocalChannelControl);
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = group;
            group.SetPttActive(true);
        }
        else if (transmitCoordinator.ActiveChannel is null)
        {
            pttActivationArbiter.Clear();
        }
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
        channelSelection.ClearIfChannelUnavailable();
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
