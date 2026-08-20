using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using fnecore.P25;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal const double ChannelWidgetSpacing = 8;
    internal const double DefaultWidgetCanvasWidth = 900;
    private const int MaximumSubscriberCommandAuditEntries = 50;
    private const int RecordingCatalogUiBatchSize = 64;
    private const string DvmConsoleProcessingDisplay = "DVM Console processing";
    private const string AppleVoiceProcessingDisplay = "Apple voice processing";
    private static readonly string[] AppleAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay, AppleVoiceProcessingDisplay];
    private static readonly string[] DvmConsoleAudioProcessingModeOptions =
        [DvmConsoleProcessingDisplay];
    private static readonly KeyboardPttKey[] GlobalPttKeyOptionValues = Enum.GetValues<KeyboardPttKey>();
    private static readonly int[] SerialPttBaudRateOptions = [1_200, 2_400, 4_800, 9_600, 19_200, 38_400, 57_600, 115_200];
    private readonly ChannelReceiveAudioCoordinator audioCoordinator;
    private readonly ChannelReceiveWorkQueue receiveAudioWork;
    private readonly UserSettingsStore userSettingsStore;
    private readonly UserSettings userSettings;
    private readonly string codeplugDiagnosticsText;
    private readonly ChannelTransmitCoordinator transmitCoordinator;
    private readonly DefaultAudioDeviceMonitor defaultAudioDeviceMonitor;
    private readonly LatestBooleanStateReconciler warmMicrophoneReconciler;
    private readonly ToneTransmitCoordinator toneTransmitCoordinator;
    private readonly LocalTonePlayer localTonePlayer;
    private readonly PatchForwardingCoordinator patchForwarding;
    private readonly PatchSourceDecodeCoordinator patchSourceDecode;
    private readonly P25KeyRing? p25KeyRing;
    private readonly DmrKeyRing? dmrKeyRing;
    private readonly NxdnKeyRing? nxdnKeyRing;
    private KeyboardPttSource keyboardPtt;
    private GlobalKeyboardPttSource? globalKeyboardPtt;
    private IPttSource? serialPtt;
    private readonly Func<string, int, IPttSource> serialPttFactory;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly SemaphoreSlim serialPttChangeLock = new(1, 1);
    private readonly ObservableCollection<string> serialPttPortOptions = [];
    private readonly CallHistoryStore callHistory = new();
    private readonly ObservableCollection<CallHistoryEntry> filteredCallHistoryEntries = [];
    private readonly ObservableCollection<CallHistoryEntry> activityCallHistoryEntries = [];
    private readonly ObservableCollection<CallRecordingMetadata> recordingEntries = [];
    private readonly object recordingCatalogScanSync = new();
    private CancellationTokenSource? recordingCatalogScanCancellation;
    private int recordingCatalogScanGeneration;
    private long recordingCatalogMutationRevision;
    private Task recordingCatalogScanTask = Task.CompletedTask;
    private readonly ObservableCollection<DtmfPresetViewModel> dtmfPresets = [];
    private readonly ObservableCollection<TonePresetViewModel> tonePresets = [];
    private readonly ObservableCollection<ToneSequenceStepViewModel> toneSequenceSteps = [];
    private readonly ObservableCollection<AlertToneViewModel> alertTones = [];
    private readonly ObservableCollection<BuiltInAlertToneViewModel> builtInAlertTones = [];
    private readonly ObservableCollection<ToolbarClockViewModel> toolbarClocks = [];
    private readonly ObservableCollection<AudioInputPresetViewModel> audioInputPresets = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioInputDevices = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioOutputDevices = [];
    private readonly ObservableCollection<SubscriberCommandAuditEntry> subscriberCommandAudit = [];
    private readonly ObservableCollection<DebugLogEntry> debugLogEntries = [];
    private readonly ObservableCollection<string> recentCodeplugPaths = [];
    private readonly ObservableCollection<WebStreamViewModel> webStreams = [];
    private readonly WebStreamPlaybackCoordinator webStreamPlayback;
    private readonly object patchSourceWorkSync = new();
    private readonly Dictionary<ChannelViewModel, Task> patchSourceWork = [];
    private readonly object systemTrafficWorkSync = new();
    private readonly Dictionary<SystemViewModel, SystemTrafficBuffer> pendingSystemTraffic = [];
    private readonly HashSet<SystemViewModel> scheduledSystemTraffic = [];
    private readonly object audioLevelLogSync = new();
    private readonly Dictionary<(ChannelViewModel Channel, ChannelAudioDirection Direction), DateTimeOffset> lastAudioLevelLogs = [];
    private readonly ChannelAudioMeterPipeline audioMeterPipeline = new();
    private readonly ReceiveDiagnosticsReporter receiveDiagnosticsReporter = new(TimeSpan.FromMilliseconds(500));
    private readonly SemaphoreSlim audioReconfigurationLock = new(1, 1);
    private readonly Dictionary<ChannelViewModel, DateTimeOffset> receiveRetryAfter = [];
    private readonly Dictionary<string, FneConnectionState> lastConnectionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<SystemViewModel, IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>> trafficRoutes;
    private readonly ConnectionChimeTracker connectionChimeTracker = new();
    private ChannelViewModel[] suspendedAudioChannels = [];
    private bool suspendedAudioKeptActive;
    private bool activityCurrentZoneOnly;
    private PatchGroupEditorViewModel? activeMultiSelectGroup;
    private readonly CallRecordingManager callRecordings;
    private readonly RecordingPlaybackCoordinator recordingPlayback;
    private readonly DispatcherTimer clockTimer;
    private readonly DispatcherTimer audioMeterTimer;
    private Bitmap? userBackgroundBitmap;
    private int disposeStarted;
    private IBrush mainBackgroundBrush = new SolidColorBrush(Color.Parse("#0D1116"));
    private string statusText;
    private string audioStatusText = "RX audio disabled.";
    private string transmitStatusText = "PTT idle.";
    private string dtmfDigits = "123";
    private string toneFrequencyText = "1000";
    private string toneDurationText = "1.0";
    private string quickCallToneAText = "600";
    private string quickCallToneBText = "1200";
    private string audioInputDeviceIdText = "default";
    private string audioOutputDeviceIdText = "default";
    private string audioInputGainText = "1.0";
    private string audioInputLowGainText = "0";
    private string audioInputMidGainText = "0";
    private string audioInputHighGainText = "0";
    private string audioInputAgcTargetDbfsText = "-25";
    private bool audioInputAgcEnabled;
    private bool highQualityBluetoothAudioEnabled;
    private string selectedAudioProcessingMode = "DVM Console processing";
    private KeyboardPttKey selectedGlobalPttKey;
    private string audioInputPresetNameText = string.Empty;
    private string dtmfPresetName = string.Empty;
    private string tonePresetName = string.Empty;
    private string alertToneNameText = string.Empty;
    private string recordingRetentionDaysText = string.Empty;
    private string recordingRootPathText = string.Empty;
    private string recordingDirectionFilter = "All";
    private string recordingProtocolFilter = "All";
    private string recordingEncryptionFilter = "All";
    private string recordingSystemFilterText = string.Empty;
    private string recordingChannelFilterText = string.Empty;
    private string recordingTalkgroupFilterText = string.Empty;
    private string recordingSubscriberFilterText = string.Empty;
    private string recordingAliasFilterText = string.Empty;
    private DateTimeOffset? recordingStartDateFilter;
    private DateTimeOffset? recordingEndDateFilter;
    private bool recordingTimeColumnVisible = true;
    private bool recordingDurationColumnVisible = true;
    private bool recordingChannelColumnVisible = true;
    private bool recordingTalkgroupColumnVisible = true;
    private bool recordingSourceIdColumnVisible = true;
    private bool recordingAliasColumnVisible = true;
    private bool recordingDirectionColumnVisible;
    private bool recordingProtocolColumnVisible;
    private bool recordingSystemColumnVisible;
    private bool recordingEncryptionColumnVisible;
    private bool recordingDiagnosticsColumnVisible = true;
    private string clockText = string.Empty;
    private string debugLogFilterText = string.Empty;
    private string debugLogSeverityFilter = "Info";
    private string callHistoryFilterText = string.Empty;
    private string recordingFilterText = string.Empty;
    private bool busy;
    private bool codeplugDiagnosticsDismissed;
    private bool pttStarted;
    private bool serialPttEnabled;
    private string serialPttPortName = string.Empty;
    private int serialPttBaudRate = 9_600;
    private string serialPttStatusText = "Serial PTT is disabled.";
    private ChannelViewModel? selectedChannel;
    private SystemViewModel? selectedSystem;
    private AudioDeviceOptionViewModel? selectedAudioInputDevice;
    private AudioDeviceOptionViewModel? selectedAudioOutputDevice;
    private readonly ScaleTransform uiScaleTransform;

    private MainWindowViewModel(
        string statusText,
        IEnumerable<SystemViewModel> systems,
        IEnumerable<ZoneViewModel> zones,
        IP25KeyResolver? p25KeyResolver = null,
        UserSettingsStore? userSettingsStore = null,
        IEnumerable<GroupConfiguration>? groupDefinitions = null,
        bool patchSourceIdPassthrough = false,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.statusText = statusText;
        codeplugDiagnosticsText = statusText;
        this.userSettingsStore = userSettingsStore ?? new UserSettingsStore(UserSettingsStore.DefaultPath);
        userSettings = this.userSettingsStore.Load();
        this.serialPortProvider = serialPortProvider ?? SerialPttSource.GetAvailablePortNames;
        this.serialPttFactory = serialPttFactory ?? ((portName, baudRate) => new SerialPttSource(portName, baudRate));
        uiScaleTransform = new ScaleTransform
        {
            ScaleX = userSettings.UiScale,
            ScaleY = userSettings.UiScale
        };
        foreach (string path in userSettings.RecentCodeplugPaths.Take(UserSettings.MaximumRecentCodeplugs))
            recentCodeplugPaths.Add(path);
        LoadUserBackground(userSettings.UserBackgroundImage);
        ApplyTheme(userSettings.DarkMode);
        keyboardPtt = new KeyboardPttSource(ParseGlobalPttKey(userSettings.GlobalPttKey))
        {
            ToggleMode = userSettings.TogglePttMode
        };
        selectedGlobalPttKey = keyboardPtt.ActivationKey;
        serialPttEnabled = userSettings.SerialPttEnabled;
        serialPttPortName = userSettings.SerialPttPortName;
        serialPttBaudRate = userSettings.SerialPttBaudRate;
        string? environmentSerialPort = Environment.GetEnvironmentVariable("DVM_PTT_SERIAL_PORT");
        if (serialPttPortName.Length == 0 && !string.IsNullOrWhiteSpace(environmentSerialPort))
        {
            serialPttEnabled = true;
            serialPttPortName = environmentSerialPort.Trim();
            serialPttBaudRate = ReadSerialPttBaudRate();
        }
        RefreshSerialPttDevices();
        if (serialPttEnabled && serialPttPortName.Length > 0)
        {
            serialPtt = this.serialPttFactory(serialPttPortName, serialPttBaudRate);
            serialPttStatusText = $"Configured for {serialPttPortName} at {serialPttBaudRate:N0} baud.";
        }
        clockText = FormatClock(DateTime.Now, userSettings.ClockUse24HourTime, userSettings.ClockShowSeconds);
        clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        clockTimer.Tick += HandleClockTick;
        clockTimer.Start();
        audioMeterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ChannelAudioMeterPipeline.RefreshIntervalMilliseconds)
        };
        audioMeterTimer.Tick += HandleAudioMeterTick;
        audioMeterTimer.Start();
        dtmfDigits = userSettings.LastDtmfDigits;
        toneFrequencyText = userSettings.ToneFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        toneDurationText = userSettings.ToneDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        toneSequenceSteps.Add(new ToneSequenceStepViewModel(
            userSettings.ToneFrequencyHz,
            userSettings.ToneDurationSeconds));
        quickCallToneAText = userSettings.QuickCallToneAFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        quickCallToneBText = userSettings.QuickCallToneBFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputDeviceIdText = userSettings.AudioInputDeviceId;
        audioOutputDeviceIdText = userSettings.AudioOutputDeviceId;
        audioInputGainText = userSettings.AudioInputGain.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputLowGainText = userSettings.AudioInputEqLowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputMidGainText = userSettings.AudioInputEqMidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputHighGainText = userSettings.AudioInputEqHighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputAgcTargetDbfsText = userSettings.AudioInputAgcTargetDbfs.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputAgcEnabled = userSettings.AudioInputAgcEnabled;
        highQualityBluetoothAudioEnabled = userSettings.HighQualityBluetoothAudioEnabled;
        selectedAudioProcessingMode = ToAudioProcessingModeDisplay(userSettings.AudioProcessingMode);
        audioInputPresetNameText = userSettings.AudioInputPresetName;
        recordingRetentionDaysText = userSettings.RecordingRetentionDays.ToString(CultureInfo.InvariantCulture);
        recordingRootPathText = GetDefaultRecordingRoot(userSettings.RecordingRootPath);
        webStreamPlayback = new WebStreamPlaybackCoordinator(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => userSettings.AudioOutputDeviceId,
            getStreamOutputDeviceId: GetWebStreamOutputDeviceId);
        foreach (DtmfPresetSetting preset in userSettings.DtmfPresets)
            dtmfPresets.Add(new DtmfPresetViewModel(preset));
        foreach (TonePresetSetting preset in userSettings.TonePresets)
            tonePresets.Add(new TonePresetViewModel(preset));
        foreach (AlertToneSetting tone in userSettings.AlertTones)
            alertTones.Add(new AlertToneViewModel(tone));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert1));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert2));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert3));
        List<ToolbarClockSetting> configuredClocks = (userSettings.ToolbarClocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .ToList();
        while (configuredClocks.Count < UserSettings.MaximumToolbarClocks)
            configuredClocks.Add(new ToolbarClockSetting());
        for (int index = 0; index < configuredClocks.Count; index++)
            toolbarClocks.Add(new ToolbarClockViewModel(index + 1, configuredClocks[index]));
        RefreshClock();
        foreach (AudioInputPresetSetting preset in userSettings.AudioInputPresets)
            audioInputPresets.Add(new AudioInputPresetViewModel(preset));
        p25KeyRing = p25KeyResolver as P25KeyRing;
        dmrKeyRing = dmrKeyResolver as DmrKeyRing;
        nxdnKeyRing = nxdnKeyResolver as NxdnKeyRing;
        callRecordings = new CallRecordingManager(
            recordingRootPathText,
            HandleRecordingFaulted,
            userSettings.RecordingRetentionDays,
            ShouldRecordSource);
        callRecordings.RecordingFinalized += HandleRecordingFinalized;
        recordingPlayback = new RecordingPlaybackCoordinator(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => userSettings.AudioOutputDeviceId,
            HandleRecordingPlaybackFaulted);
        audioCoordinator = new ChannelReceiveAudioCoordinator(
            CreateReceiveAudioBackend,
            () => new SoftwareVocoderBackend(),
            p25KeyResolver,
            HandleDecodedSamples,
            GetChannelVolume,
            GetChannelOutputDeviceId,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver,
            getChannelBalance: GetChannelStereoBalance);
        receiveAudioWork = new ChannelReceiveWorkQueue(ProcessAudioAsync);
        transmitCoordinator = new ChannelTransmitCoordinator(
            p25KeyResolver,
            new AudioInputProcessingOptions
            {
                DeviceId = userSettings.AudioInputDeviceId,
                ProcessingMode = GetConfiguredAudioProcessingMode(),
                AgcEnabled = userSettings.AudioInputAgcEnabled,
                AgcTargetDbfs = userSettings.AudioInputAgcTargetDbfs,
                Gain = userSettings.AudioInputGain,
                LowGainDb = userSettings.AudioInputEqLowGainDb,
                MidGainDb = userSettings.AudioInputEqMidGainDb,
                HighGainDb = userSettings.AudioInputEqHighGainDb
            },
            HandleTransmitSamples,
            CreateTransmitAudioBackend,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        warmMicrophoneReconciler = new LatestBooleanStateReconciler(
            transmitCoordinator.SetKeepMicrophoneWarmAsync);
        warmMicrophoneReconciler.Reconciled += HandleWarmMicrophoneReconciled;
        transmitCoordinator.HighQualityBluetoothStatusChanged += HandleHighQualityBluetoothStatusChanged;
        if (userSettings.KeepTransmitMicrophoneWarm)
            _ = warmMicrophoneReconciler.SetDesired(true);
        toneTransmitCoordinator = new ToneTransmitCoordinator(
            p25KeyResolver,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        localTonePlayer = new LocalTonePlayer(
            CreateTransmitAudioBackend,
            () => userSettings.AudioOutputDeviceId);
        Systems = systems.ToArray();
        Zones = zones.ToArray();
        trafficRoutes = Systems.ToDictionary(
            system => system,
            system => (IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]>)system.Channels
                .GroupBy(channel => (ProtocolFor(channel), channel.Definition.DestinationId))
                .ToDictionary(group => group.Key, group => group.ToArray()));
        RestoreChannelWidgetLayout();
        foreach (ZoneViewModel zone in Zones)
            zone.SetWidgetCardHeight(ChannelCardHeight);
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
            channel.SetDarkMode(userSettings.DarkMode);
        foreach (ZoneViewModel zone in Zones)
            zone.SetDarkMode(userSettings.DarkMode);
        GroupConfiguration[] configuredGroups = (groupDefinitions ?? []).ToArray();
        patchForwarding = new PatchForwardingCoordinator(
            Systems,
            p25KeyResolver,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver)
        {
            SourceIdPassthrough = patchSourceIdPassthrough
        };
        patchSourceDecode = new PatchSourceDecodeCoordinator(
            p25KeyResolver,
            ObservePatchDecodedSamples,
            dmrKeyResolver: dmrKeyResolver,
            nxdnKeyResolver: nxdnKeyResolver);
        RestorePatchState(configuredGroups);
        PatchGroups = BuildPatchGroups(configuredGroups);
        RefreshPatchMembershipConflicts();
        CallHistory = new System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry>(callHistory.Entries);
        FilteredCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(filteredCallHistoryEntries);
        ActivityCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(activityCallHistoryEntries);
        Recordings = new ReadOnlyObservableCollection<CallRecordingMetadata>(recordingEntries);
        DtmfPresets = new ReadOnlyObservableCollection<DtmfPresetViewModel>(dtmfPresets);
        TonePresets = new ReadOnlyObservableCollection<TonePresetViewModel>(tonePresets);
        ToneSequenceSteps = new ReadOnlyObservableCollection<ToneSequenceStepViewModel>(toneSequenceSteps);
        AlertTones = new ReadOnlyObservableCollection<AlertToneViewModel>(alertTones);
        BuiltInAlertTones = new ReadOnlyObservableCollection<BuiltInAlertToneViewModel>(builtInAlertTones);
        ToolbarClocks = new ReadOnlyObservableCollection<ToolbarClockViewModel>(toolbarClocks);
        AudioInputPresets = new ReadOnlyObservableCollection<AudioInputPresetViewModel>(audioInputPresets);
        AudioInputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioInputDevices);
        AudioOutputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioOutputDevices);
        SubscriberCommandAudit = new ReadOnlyObservableCollection<SubscriberCommandAuditEntry>(subscriberCommandAudit);
        DebugLogEntries = new ReadOnlyObservableCollection<DebugLogEntry>(debugLogEntries);
        RecentCodeplugPaths = new ReadOnlyObservableCollection<string>(recentCodeplugPaths);
        WebStreams = new ReadOnlyObservableCollection<WebStreamViewModel>(webStreams);
        foreach (WebStreamViewModel stream in Zones.SelectMany(zone => zone.WebStreams))
        {
            stream.SetOutputDeviceOptions(AudioOutputDevices);
            stream.SetInitialVolume(
                userSettings.WebStreamVolumes.TryGetValue(stream.Name, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            stream.RestoreOutputDeviceId(
                userSettings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            stream.VolumeChanged += HandleWebStreamVolumeChanged;
            stream.PropertyChanged += HandleWebStreamPropertyChanged;
            stream.Configure(StartWebStreamAsync, StopWebStreamAsync);
            webStreams.Add(stream);
        }
        _ = RestoreSelectedWebStreamsAsync();
        RefreshRecordings(pruneExpired: true);
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.SetOutputDeviceOptions(AudioOutputDevices);
            if (channel.Definition.SelectableEncryption &&
                userSettings.TransmitEncryptionStates.TryGetValue(channel.SettingsKey, out bool savedEncryptionState))
            {
                channel.RestoreTransmitEncryption(savedEncryptionState);
            }

            channel.RestoreVolume(
                userSettings.ChannelVolumes.TryGetValue(channel.SettingsKey, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            channel.RestoreStereoBalance(
                userSettings.ChannelStereoBalances.TryGetValue(channel.SettingsKey, out double savedBalance)
                    ? savedBalance
                    : 0.0);
            channel.RestoreOutputDeviceId(
                userSettings.ChannelOutputDeviceIds.TryGetValue(channel.SettingsKey, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            channel.RestoreRecordingEnabled(userSettings.RecordingEnabledChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            channel.TransmitEncryptionChanged += HandleChannelEncryptionChanged;
            channel.RecordingStateChanged += HandleChannelRecordingChanged;
            channel.VolumeChanged += HandleChannelVolumeChanged;
            channel.StereoBalanceChanged += HandleChannelStereoBalanceChanged;
            channel.SetIgnoredSubscriberIds(
                userSettings.RecordingIgnoredSubscriberIds.TryGetValue(
                    channel.SettingsKey,
                    out List<uint>? ignoredSubscriberIds)
                    ? ignoredSubscriberIds
                    : []);
            channel.ConfigureAudio(StartAudioAsync, StopAudioAsync);
            channel.ConfigureTransmit(StartTransmitAsync, StopTransmitAsync);
            channel.RestoreTransmitSelection(userSettings.TransmitSelectedChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            if (channel.IsRecordingEnabled)
                _ = EnsureRecordingAudioAsync(channel);
        }

        foreach (SystemViewModel system in Systems)
        {
            system.PropertyChanged += HandleSystemPropertyChanged;
            system.StatusChanged += (_, status) => HandleSystemStatus(system, status);
            system.LogReceived += HandleSystemLog;
            system.TrafficReceived += (_, traffic) => HandleSystemTraffic(system, traffic);
            system.KeyResponseReceived += HandleSystemKeyResponse;
        }

        selectedChannel = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems
                .SelectMany(system => system.Channels)
                .FirstOrDefault(channel => channel.SettingsKey.Equals(
                    userSettings.LastSelectedChannelKey,
                    StringComparison.Ordinal))
            : null;
        selectedSystem = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems.FirstOrDefault(system => system.Name.Equals(
                userSettings.LastSelectedSystemName,
                StringComparison.OrdinalIgnoreCase)) ??
                Systems.FirstOrDefault(system => selectedChannel is not null && system.Channels.Contains(selectedChannel)) ??
                Systems.FirstOrDefault()
            : Systems.FirstOrDefault();
        foreach (SystemViewModel system in Systems)
            system.SetSelected(ReferenceEquals(system, selectedSystem));
        RefreshActivityCallHistory();

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !busy && Systems.Count > 0);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !busy && Systems.Count > 0);
        SendDtmfCommand = new AsyncRelayCommand(SendDtmfAsync, CanSendGeneratedAudio);
        SendToneCommand = new AsyncRelayCommand(SendToneAsync, CanSendGeneratedAudio);
        SaveDtmfPresetCommand = new RelayCommand(SaveDtmfPreset);
        SaveTonePresetCommand = new RelayCommand(SaveTonePreset);
        ApplyAudioInputSettingsCommand = new AsyncRelayCommand(
            () => ApplyAudioInputSettingsAsync(restartActiveAudio: true),
            () => !busy && transmitCoordinator.ActiveChannel is null);
        ApplyRecordingRetentionCommand = new RelayCommand(ApplyRecordingRetention);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices);
        defaultAudioDeviceMonitor = new DefaultAudioDeviceMonitor(
            new AudioBackendDeviceTopologyProvider(CreateReceiveAudioBackend),
            HandleAudioDeviceTopologyChangedAsync);
        RefreshAudioDevices();
        defaultAudioDeviceMonitor.Start();
        transmitCoordinator.Faulted += HandleTransmitFaulted;
        keyboardPtt.StateChanged += HandleKeyboardPttStateChanged;
        if (serialPtt is not null)
            serialPtt.StateChanged += HandleKeyboardPttStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public bool IsCodeplugLoaded => Systems.Count > 0;

    public string? CurrentCodeplugPath => userSettings.LastCodeplugPath;

    public string SettingsVersionText => userSettings.SchemaVersion == UserSettings.CurrentSchemaVersion
        ? $"Profile format v{userSettings.SchemaVersion}"
        : userSettings.SchemaVersion > UserSettings.CurrentSchemaVersion
            ? $"Profile format v{userSettings.SchemaVersion} (newer than this build)"
            : $"Profile format v{userSettings.SchemaVersion} (legacy)";

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

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
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
        get => dtmfDigits;
        set => SetField(ref dtmfDigits, value ?? string.Empty);
    }

    public string ToneFrequencyText
    {
        get => toneFrequencyText;
        set => SetField(ref toneFrequencyText, value ?? string.Empty);
    }

    public string ToneDurationText
    {
        get => toneDurationText;
        set => SetField(ref toneDurationText, value ?? string.Empty);
    }

    public string AudioInputDeviceIdText
    {
        get => audioInputDeviceIdText;
        set => SetField(ref audioInputDeviceIdText, value ?? string.Empty);
    }

    public string AudioOutputDeviceIdText
    {
        get => audioOutputDeviceIdText;
        set => SetField(ref audioOutputDeviceIdText, value ?? string.Empty);
    }

    public AudioDeviceOptionViewModel? SelectedAudioInputDevice
    {
        get => selectedAudioInputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioInputDevice, value))
                return;
            selectedAudioInputDevice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            if (value is not null)
                AudioInputDeviceIdText = value.Id;
            RefreshAppleVoiceProcessingRouteState();
        }
    }

    public AudioDeviceOptionViewModel? SelectedAudioOutputDevice
    {
        get => selectedAudioOutputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioOutputDevice, value))
                return;
            selectedAudioOutputDevice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
            if (value is not null)
                AudioOutputDeviceIdText = value.Id;
            RefreshAppleVoiceProcessingRouteState();
        }
    }

    public string AudioInputGainText
    {
        get => audioInputGainText;
        set => SetField(ref audioInputGainText, value ?? string.Empty);
    }

    public string AudioInputLowGainText
    {
        get => audioInputLowGainText;
        set => SetField(ref audioInputLowGainText, value ?? string.Empty);
    }

    public string AudioInputMidGainText
    {
        get => audioInputMidGainText;
        set => SetField(ref audioInputMidGainText, value ?? string.Empty);
    }

    public string AudioInputHighGainText
    {
        get => audioInputHighGainText;
        set => SetField(ref audioInputHighGainText, value ?? string.Empty);
    }

    public bool AudioInputAgcEnabled
    {
        get => audioInputAgcEnabled;
        set
        {
            if (audioInputAgcEnabled == value)
                return;
            SetField(ref audioInputAgcEnabled, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgcTargetEnabled)));
        }
    }

    public string AudioInputAgcTargetDbfsText
    {
        get => audioInputAgcTargetDbfsText;
        set => SetField(ref audioInputAgcTargetDbfsText, value ?? string.Empty);
    }

    public bool HighQualityBluetoothAudioEnabled
    {
        get => highQualityBluetoothAudioEnabled;
        set => SetField(ref highQualityBluetoothAudioEnabled, value);
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

    public IReadOnlyList<string> AudioProcessingModeOptions
        => IsAppleVoiceProcessingPlatformAvailable && IsAppleVoiceProcessingRouteCompatible
            ? AppleAudioProcessingModeOptions
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
        get => selectedAudioProcessingMode;
        set
        {
            string normalized = IsAppleVoiceProcessingPlatformAvailable &&
                IsAppleVoiceProcessingRouteCompatible &&
                value == AppleVoiceProcessingDisplay
                ? AppleVoiceProcessingDisplay
                : DvmConsoleProcessingDisplay;
            if (selectedAudioProcessingMode == normalized)
                return;
            SetField(ref selectedAudioProcessingMode, normalized);
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
        => IsDvmConsoleProcessingSelected
            ? "DVM Console applies its gain, EQ, and optional AGC after microphone capture."
            : "Apple Voice Processing applies acoustic echo cancellation and automatic gain control to the microphone capture used for transmit. Receive audio remains unprocessed.";

    public string AudioInputPresetNameText
    {
        get => audioInputPresetNameText;
        set => SetField(ref audioInputPresetNameText, value ?? string.Empty);
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
        get => userSettings.TogglePttMode;
        set
        {
            if (userSettings.TogglePttMode == value)
                return;
            userSettings.TogglePttMode = value;
            keyboardPtt.ToggleMode = value;
            if (globalKeyboardPtt is not null)
                globalKeyboardPtt.ToggleMode = value;
            PersistUserSettings();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TogglePttMode)));
        }
    }

    public string GlobalPttKeyText => keyboardPtt.ActivationKey == KeyboardPttKey.None
        ? "Keyboard PTT disabled"
        : keyboardPtt.ActivationKey.ToString();

    public IReadOnlyList<KeyboardPttKey> GlobalPttKeyOptions => GlobalPttKeyOptionValues;

    public KeyboardPttKey SelectedGlobalPttKey
    {
        get => selectedGlobalPttKey;
        set => SetField(ref selectedGlobalPttKey, value);
    }

    public Task ApplyGlobalPttKeySelectionAsync()
        => SetGlobalPttKeyAsync(SelectedGlobalPttKey);

    public bool SerialPttEnabled
    {
        get => serialPttEnabled;
        set => SetField(ref serialPttEnabled, value);
    }

    public string SerialPttPortName
    {
        get => serialPttPortName;
        set => SetField(ref serialPttPortName, value?.Trim() ?? string.Empty);
    }

    public int SerialPttBaudRate
    {
        get => serialPttBaudRate;
        set => SetField(ref serialPttBaudRate, value);
    }

    public IReadOnlyList<string> SerialPttPortOptions => serialPttPortOptions;

    public IReadOnlyList<int> SerialPttBaudRates
        => SerialPttBaudRateOptions
            .Append(SerialPttBaudRate)
            .Where(baudRate => baudRate > 0)
            .Distinct()
            .Order()
            .ToArray();

    public string SerialPttStatusText
    {
        get => serialPttStatusText;
        private set => SetField(ref serialPttStatusText, value);
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
            serialPttPortOptions.Clear();
            foreach (string device in devices)
                serialPttPortOptions.Add(device);

            if (SerialPttPortName.Length == 0 && devices.Length > 0)
                SerialPttPortName = devices[0];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialPttPortOptions)));
            SerialPttStatusText = serialPtt is not null && SerialPttEnabled
                ? $"Serial PTT configured for {SerialPttPortName} at {SerialPttBaudRate:N0} baud."
                : devices.Length == 0
                    ? "Serial PTT is disabled; no serial devices were detected."
                    : $"Serial PTT is disabled; detected {devices.Length} serial device(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            serialPttPortOptions.Clear();
            if (SerialPttPortName.Length > 0)
                serialPttPortOptions.Add(SerialPttPortName);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialPttPortOptions)));
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

        await serialPttChangeLock.WaitAsync();
        try
        {
            IPttSource? previous = serialPtt;
            serialPtt = null;
            if (previous is not null)
                await StopAndDisposeSerialPttAsync(previous);

            userSettings.SerialPttEnabled = SerialPttEnabled;
            userSettings.SerialPttPortName = portName;
            userSettings.SerialPttBaudRate = baudRate;
            PersistUserSettings();
            if (!SerialPttEnabled)
            {
                SerialPttStatusText = "Serial PTT is disabled.";
                TransmitStatusText = "PTT idle; serial hardware source disabled.";
                return true;
            }

            IPttSource? candidate = null;
            try
            {
                candidate = serialPttFactory(portName, baudRate);
                candidate.StateChanged += HandleKeyboardPttStateChanged;
                if (pttStarted)
                    await candidate.StartAsync();
                serialPtt = candidate;
                SerialPttStatusText = pttStarted
                    ? $"Serial PTT ready on {portName} at {baudRate:N0} baud."
                    : $"Serial PTT configured for {portName} at {baudRate:N0} baud.";
                TransmitStatusText = pttStarted
                    ? $"PTT idle; serial source {portName} ready."
                    : $"PTT idle; serial source {portName} will start with global PTT.";
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                if (candidate is not null)
                {
                    candidate.StateChanged -= HandleKeyboardPttStateChanged;
                    await candidate.DisposeAsync();
                }
                SerialPttStatusText = $"Serial PTT unavailable on {portName}: {exception.Message}";
                TransmitStatusText = $"PTT idle; serial source unavailable: {exception.Message}";
                return false;
            }
        }
        finally
        {
            serialPttChangeLock.Release();
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
        get => dtmfPresetName;
        set => SetField(ref dtmfPresetName, value ?? string.Empty);
    }

    public string TonePresetName
    {
        get => tonePresetName;
        set => SetField(ref tonePresetName, value ?? string.Empty);
    }

    public string QuickCallToneAText
    {
        get => quickCallToneAText;
        set => SetField(ref quickCallToneAText, value ?? string.Empty);
    }

    public string QuickCallToneBText
    {
        get => quickCallToneBText;
        set => SetField(ref quickCallToneBText, value ?? string.Empty);
    }

    public string AlertToneNameText
    {
        get => alertToneNameText;
        set => SetField(ref alertToneNameText, value ?? string.Empty);
    }

    public string RecordingRetentionDaysText
    {
        get => recordingRetentionDaysText;
        set => SetField(ref recordingRetentionDaysText, value ?? string.Empty);
    }

    public string RecordingRootPathText
    {
        get => recordingRootPathText;
        set => SetField(ref recordingRootPathText, value ?? string.Empty);
    }

    public string SelectionStatusText => selectedChannel is null
        ? keyboardPtt.ActivationKey == KeyboardPttKey.None
            ? "Choose TX on one or more cards. Keyboard PTT is disabled."
            : $"Choose TX on one or more cards, then hold {GlobalPttKeyText}."
        : $"RX focus: {selectedChannel.Name}. Global PTT: {GlobalPttKeyText}.";

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
    public ReadOnlyObservableCollection<DtmfPresetViewModel> DtmfPresets { get; }
    public ReadOnlyObservableCollection<TonePresetViewModel> TonePresets { get; }
    public ReadOnlyObservableCollection<ToneSequenceStepViewModel> ToneSequenceSteps { get; }
    public ReadOnlyObservableCollection<AlertToneViewModel> AlertTones { get; }
    public ReadOnlyObservableCollection<BuiltInAlertToneViewModel> BuiltInAlertTones { get; }
    public ReadOnlyObservableCollection<ToolbarClockViewModel> ToolbarClocks { get; }
    public ReadOnlyObservableCollection<AudioInputPresetViewModel> AudioInputPresets { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioInputDevices { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioOutputDevices { get; }
    public ReadOnlyObservableCollection<SubscriberCommandAuditEntry> SubscriberCommandAudit { get; }
    public ReadOnlyObservableCollection<DebugLogEntry> DebugLogEntries { get; }
    public ReadOnlyObservableCollection<WebStreamViewModel> WebStreams { get; }
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<CallHistoryEntry> CallHistory { get; }
    public ReadOnlyObservableCollection<CallHistoryEntry> ActivityCallHistory { get; }
    public string ActivityFilterButtonText => activityCurrentZoneOnly ? "Current tab" : "All channels";
    public IReadOnlyList<SubscriberCommandAuditEntry> ActivitySubscriberCommandAudit
        => SelectedSystem is null
            ? []
            : SubscriberCommandAudit
                .Where(entry => entry.SystemName.Equals(SelectedSystem.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    public ReadOnlyObservableCollection<CallHistoryEntry> FilteredCallHistory { get; }
    public bool HasAdvancedHistoryFilters =>
        RecordingDirectionFilter != "All" ||
        RecordingProtocolFilter != "All" ||
        RecordingEncryptionFilter != "All" ||
        !string.IsNullOrWhiteSpace(RecordingSystemFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingChannelFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingTalkgroupFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingSubscriberFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingAliasFilterText) ||
        RecordingStartDateFilter is not null ||
        RecordingEndDateFilter is not null;
    public string HistoryFilterSummary
    {
        get
        {
            var filters = new List<string>();
            if (RecordingDirectionFilter != "All") filters.Add(RecordingDirectionFilter);
            if (RecordingProtocolFilter != "All") filters.Add(RecordingProtocolFilter);
            if (RecordingEncryptionFilter != "All") filters.Add(RecordingEncryptionFilter);
            if (!string.IsNullOrWhiteSpace(RecordingSystemFilterText)) filters.Add($"system {RecordingSystemFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingChannelFilterText)) filters.Add($"channel {RecordingChannelFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingTalkgroupFilterText)) filters.Add($"TG {RecordingTalkgroupFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingSubscriberFilterText)) filters.Add($"RID {RecordingSubscriberFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingAliasFilterText)) filters.Add($"alias {RecordingAliasFilterText}");
            if (RecordingStartDateFilter is DateTimeOffset start) filters.Add($"from {start:yyyy-MM-dd}");
            if (RecordingEndDateFilter is DateTimeOffset end) filters.Add($"to {end:yyyy-MM-dd}");
            return string.Join(" · ", filters);
        }
    }
    public ReadOnlyObservableCollection<CallRecordingMetadata> Recordings { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendDtmfCommand { get; }
    public ICommand SendToneCommand { get; }
    public ICommand SaveDtmfPresetCommand { get; }
    public ICommand SaveTonePresetCommand { get; }
    public ICommand ApplyAudioInputSettingsCommand { get; }
    public ICommand ApplyRecordingRetentionCommand { get; }
    public ICommand RefreshAudioDevicesCommand { get; }
    public ICommand ConnectionCommand => SelectedSystem?.IsConnected == true ? DisconnectCommand : ConnectCommand;
    public string ConnectionButtonText => SelectedSystem?.IsConnected == true ? "Disconnect" : "Connect";
    public string ConnectionPillText => SelectedSystem?.IsConnected == true ? "CONNECTED" : "OFFLINE";
    public string SelectedSystemName => SelectedSystem?.Name ?? "No system";
    public string SystemStatusText => SelectedSystem?.ConnectionStatus ?? "No configured system";
    public IReadOnlyList<string> DebugLogSeverityFilters { get; } = ["All", "Debug", "Info", "Warning", "Error", "Fatal"];
    public IReadOnlyList<DebugLogEntry> FilteredDebugLogs
        => DebugLogEntries
            .Where(entry =>
                (DebugLogSeverityFilter == "All" || entry.Severity.ToString().Equals(DebugLogSeverityFilter, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(DebugLogFilterText) || entry.Summary.Contains(DebugLogFilterText, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    public string DebugLogFilterText
    {
        get => debugLogFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (debugLogFilterText == normalized)
                return;
            debugLogFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugLogFilterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }
    }

    public string DebugLogSeverityFilter
    {
        get => debugLogSeverityFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (debugLogSeverityFilter == normalized)
                return;
            debugLogSeverityFilter = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DebugLogSeverityFilter)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }
    }

    public string CallHistoryFilterText
    {
        get => callHistoryFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (callHistoryFilterText == normalized)
                return;
            callHistoryFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CallHistoryFilterText)));
            RefreshFilteredCallHistory();
        }
    }

    public string RecordingFilterText
    {
        get => recordingFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (recordingFilterText == normalized)
                return;
            recordingFilterText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingFilterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
        }
    }

    public IReadOnlyList<string> RecordingDirectionFilters { get; } = ["All", "RX", "TX"];
    public IReadOnlyList<string> RecordingProtocolFilters { get; } = ["All", "DMR", "P25", "ANALOG", "NXDN"];
    public IReadOnlyList<string> RecordingEncryptionFilters { get; } = ["All", "Clear", "Encrypted"];

    public string RecordingDirectionFilter
    {
        get => recordingDirectionFilter;
        set => SetRecordingFilter(ref recordingDirectionFilter, value, nameof(RecordingDirectionFilter));
    }

    public string RecordingProtocolFilter
    {
        get => recordingProtocolFilter;
        set => SetRecordingFilter(ref recordingProtocolFilter, value, nameof(RecordingProtocolFilter));
    }

    public string RecordingEncryptionFilter
    {
        get => recordingEncryptionFilter;
        set => SetRecordingFilter(ref recordingEncryptionFilter, value, nameof(RecordingEncryptionFilter));
    }

    public string RecordingSystemFilterText
    {
        get => recordingSystemFilterText;
        set => SetRecordingFilter(ref recordingSystemFilterText, value, nameof(RecordingSystemFilterText), allowEmpty: true);
    }

    public string RecordingChannelFilterText
    {
        get => recordingChannelFilterText;
        set => SetRecordingFilter(ref recordingChannelFilterText, value, nameof(RecordingChannelFilterText), allowEmpty: true);
    }

    public string RecordingTalkgroupFilterText
    {
        get => recordingTalkgroupFilterText;
        set => SetRecordingFilter(ref recordingTalkgroupFilterText, value, nameof(RecordingTalkgroupFilterText), allowEmpty: true);
    }

    public string RecordingSubscriberFilterText
    {
        get => recordingSubscriberFilterText;
        set => SetRecordingFilter(ref recordingSubscriberFilterText, value, nameof(RecordingSubscriberFilterText), allowEmpty: true);
    }

    public string RecordingAliasFilterText
    {
        get => recordingAliasFilterText;
        set => SetRecordingFilter(ref recordingAliasFilterText, value, nameof(RecordingAliasFilterText), allowEmpty: true);
    }

    public DateTimeOffset? RecordingStartDateFilter
    {
        get => recordingStartDateFilter;
        set => SetRecordingDateFilter(ref recordingStartDateFilter, value, nameof(RecordingStartDateFilter));
    }

    public DateTimeOffset? RecordingEndDateFilter
    {
        get => recordingEndDateFilter;
        set => SetRecordingDateFilter(ref recordingEndDateFilter, value, nameof(RecordingEndDateFilter));
    }

    public bool ShowRecordingTimeColumn
    {
        get => recordingTimeColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingTimeColumnVisible, value, nameof(ShowRecordingTimeColumn));
    }

    public bool ShowRecordingDurationColumn
    {
        get => recordingDurationColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDurationColumnVisible, value, nameof(ShowRecordingDurationColumn));
    }

    public bool ShowRecordingChannelColumn
    {
        get => recordingChannelColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingChannelColumnVisible, value, nameof(ShowRecordingChannelColumn));
    }

    public bool ShowRecordingTalkgroupColumn
    {
        get => recordingTalkgroupColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingTalkgroupColumnVisible, value, nameof(ShowRecordingTalkgroupColumn));
    }

    public bool ShowRecordingSourceIdColumn
    {
        get => recordingSourceIdColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingSourceIdColumnVisible, value, nameof(ShowRecordingSourceIdColumn));
    }

    public bool ShowRecordingAliasColumn
    {
        get => recordingAliasColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingAliasColumnVisible, value, nameof(ShowRecordingAliasColumn));
    }

    public bool ShowRecordingDirectionColumn
    {
        get => recordingDirectionColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDirectionColumnVisible, value, nameof(ShowRecordingDirectionColumn));
    }

    public bool ShowRecordingProtocolColumn
    {
        get => recordingProtocolColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingProtocolColumnVisible, value, nameof(ShowRecordingProtocolColumn));
    }

    public bool ShowRecordingSystemColumn
    {
        get => recordingSystemColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingSystemColumnVisible, value, nameof(ShowRecordingSystemColumn));
    }

    public bool ShowRecordingEncryptionColumn
    {
        get => recordingEncryptionColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingEncryptionColumnVisible, value, nameof(ShowRecordingEncryptionColumn));
    }

    public bool ShowRecordingDiagnosticsColumn
    {
        get => recordingDiagnosticsColumnVisible;
        set => SetRecordingColumnVisibility(ref recordingDiagnosticsColumnVisible, value, nameof(ShowRecordingDiagnosticsColumn));
    }

    public void ResetRecordingColumns()
    {
        ShowRecordingTimeColumn = true;
        ShowRecordingDurationColumn = true;
        ShowRecordingChannelColumn = true;
        ShowRecordingTalkgroupColumn = true;
        ShowRecordingSourceIdColumn = true;
        ShowRecordingAliasColumn = true;
        ShowRecordingDirectionColumn = false;
        ShowRecordingProtocolColumn = false;
        ShowRecordingSystemColumn = false;
        ShowRecordingEncryptionColumn = false;
        ShowRecordingDiagnosticsColumn = true;
    }

    public void ClearRecordingFilters()
    {
        RecordingFilterText = string.Empty;
        RecordingDirectionFilter = "All";
        RecordingProtocolFilter = "All";
        RecordingEncryptionFilter = "All";
        RecordingSystemFilterText = string.Empty;
        RecordingChannelFilterText = string.Empty;
        RecordingTalkgroupFilterText = string.Empty;
        RecordingSubscriberFilterText = string.Empty;
        RecordingAliasFilterText = string.Empty;
        RecordingStartDateFilter = null;
        RecordingEndDateFilter = null;
    }

    public void ClearHistoryFilters()
    {
        CallHistoryFilterText = string.Empty;
        ClearRecordingFilters();
    }

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

    public IReadOnlyList<CallRecordingMetadata> FilteredRecordings
        => Recordings
            .Where(metadata => new RecordingCatalogFilter(
                RecordingFilterText,
                RecordingDirectionFilter,
                RecordingProtocolFilter,
                RecordingEncryptionFilter,
                RecordingSystemFilterText,
                RecordingChannelFilterText,
                RecordingTalkgroupFilterText,
                RecordingSubscriberFilterText,
                RecordingAliasFilterText,
                RecordingStartDateFilter,
                RecordingEndDateFilter).Matches(metadata))
            .ToArray();

    private void SetRecordingDateFilter(
        ref DateTimeOffset? field,
        DateTimeOffset? value,
        string propertyName)
    {
        DateTimeOffset? normalized = value is DateTimeOffset date
            ? new DateTimeOffset(date.Date, date.Offset)
            : null;
        if (field == normalized)
            return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
        NotifyHistoryFilterChanged();
    }

    private void SetRecordingColumnVisibility(ref bool field, bool value, string propertyName)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetRecordingFilter(
        ref string field,
        string? value,
        string propertyName,
        bool allowEmpty = false)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? (allowEmpty ? string.Empty : "All")
            : value.Trim();
        if (field.Equals(normalized, StringComparison.Ordinal))
            return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
        NotifyHistoryFilterChanged();
    }

    private HistoryCatalogFilter CreateHistoryFilter()
        => new(
            CallHistoryFilterText,
            RecordingDirectionFilter,
            RecordingProtocolFilter,
            RecordingEncryptionFilter,
            RecordingSystemFilterText,
            RecordingChannelFilterText,
            RecordingTalkgroupFilterText,
            RecordingSubscriberFilterText,
            RecordingAliasFilterText,
            RecordingStartDateFilter,
            RecordingEndDateFilter);

    private void NotifyHistoryFilterChanged()
    {
        RefreshFilteredCallHistory();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAdvancedHistoryFilters)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryFilterSummary)));
    }

    public void ClearDebugLogs()
    {
        debugLogEntries.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        StatusText = "Debug log capture cleared.";
    }

    public void ExportDebugLogs(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string> { "Timestamp\tSeverity\tSource\tMessage" };
        lines.AddRange(DebugLogEntries.Select(entry => string.Join("\t",
            entry.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            entry.SeverityText,
            entry.Source,
            DebugLogRedactor.Redact(entry.Message).Replace("\r", " ").Replace("\n", " "))));
        File.WriteAllLines(fullPath, lines);
        StatusText = $"Exported {DebugLogEntries.Count} redacted debug log entr{(DebugLogEntries.Count == 1 ? "y" : "ies")}.";
    }
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
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
            RaiseGeneratedAudioCanExecuteChanged();
        }
    }

    public void ToggleActivityCurrentZoneFilter()
    {
        activityCurrentZoneOnly = !activityCurrentZoneOnly;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityFilterButtonText)));
        RefreshActivityCallHistory();
    }

    private void HandleSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemViewModel.SelectedZone) && ReferenceEquals(sender, SelectedSystem))
        {
            RefreshActivityCallHistory();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedZone)));
        }
    }

    public async ValueTask StartKeyboardPttAsync(CancellationToken cancellationToken = default)
    {
        if (!pttStarted)
        {
            await StartKeyboardPttSourceAsync(cancellationToken).ConfigureAwait(false);
            pttStarted = true;
        }

        await serialPttChangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (serialPtt is null)
                return;

            try
            {
                await serialPtt.StartAsync(cancellationToken).ConfigureAwait(false);
                SerialPttStatusText = $"Serial PTT ready on {SerialPttPortName} at {SerialPttBaudRate:N0} baud.";
                TransmitStatusText = $"PTT idle; serial source {SerialPttPortName} ready.";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                SerialPttStatusText = $"Serial PTT unavailable on {SerialPttPortName}: {exception.Message}";
                TransmitStatusText = $"PTT idle; serial source unavailable: {exception.Message}";
            }
        }
        finally
        {
            serialPttChangeLock.Release();
        }
    }

    private async Task StopAndDisposeSerialPttAsync(IPttSource source)
    {
        try
        {
            await source.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            source.StateChanged -= HandleKeyboardPttStateChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask StartKeyboardPttSourceAsync(CancellationToken cancellationToken)
    {
        if (keyboardPtt.ActivationKey == KeyboardPttKey.None)
        {
            TransmitStatusText = "PTT idle; keyboard PTT disabled.";
            return;
        }

        if (GlobalKeyboardPttSource.IsPlatformSupported)
        {
            var candidate = new GlobalKeyboardPttSource(keyboardPtt.ActivationKey)
            {
                ToggleMode = userSettings.TogglePttMode
            };
            candidate.StateChanged += HandleKeyboardPttStateChanged;
            try
            {
                await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
                globalKeyboardPtt = candidate;
                TransmitStatusText = $"PTT idle; OS-global {GlobalPttKeyText} ready.";
                return;
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                candidate.StateChanged -= HandleKeyboardPttStateChanged;
                await candidate.DisposeAsync().ConfigureAwait(false);
                TransmitStatusText = $"OS-global PTT unavailable; using window keyboard fallback: {exception.Message}";
            }
        }

        await keyboardPtt.StartAsync(cancellationToken).ConfigureAwait(false);
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
        if (keyboardPtt.ActivationKey == key &&
            (globalKeyboardPtt is null || globalKeyboardPtt.ActivationKey == key))
            return;
        bool keyboardWasPressed = globalKeyboardPtt?.IsPressed ?? keyboardPtt.IsPressed;
        if (keyboardWasPressed && serialPtt?.IsPressed != true)
        {
            // A release routed through the normal handler would still see the
            // old source as pressed and deliberately ignore it. Stop active TX
            // before detaching that source so rebinding can never latch PTT.
            ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
            if (active.Length > 0)
                await StopTransmitAsync(active).ConfigureAwait(false);
        }

        keyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        await keyboardPtt.DisposeAsync().ConfigureAwait(false);
        if (globalKeyboardPtt is not null)
        {
            globalKeyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
            await globalKeyboardPtt.DisposeAsync().ConfigureAwait(false);
            globalKeyboardPtt = null;
        }

        keyboardPtt = new KeyboardPttSource(key) { ToggleMode = userSettings.TogglePttMode };
        keyboardPtt.StateChanged += HandleKeyboardPttStateChanged;
        if (pttStarted)
            await StartKeyboardPttSourceAsync(CancellationToken.None).ConfigureAwait(false);
        userSettings.GlobalPttKey = key.ToString();
        PersistUserSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalPttKeyText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionStatusText)));
        TransmitStatusText = key == KeyboardPttKey.None
            ? "Keyboard global PTT disabled."
            : $"Global PTT key set to {key}.";
    }

    public async Task ToggleChannelReceiveAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        if (channel.IsAudioEnabled)
            await StopAudioAsync(channel).ConfigureAwait(false);
        else
            await StartAudioAsync(channel).ConfigureAwait(false);
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
        foreach (ChannelViewModel channel in GetReceiveScopeChannels(scope))
        {
            if (enabled && !channel.IsAudioEnabled)
                await StartAudioAsync(channel).ConfigureAwait(false);
            else if (!enabled && channel.IsAudioEnabled)
                await StopAudioAsync(channel).ConfigureAwait(false);
        }
    }

    public async Task StartChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SelectChannel(channel);
        if (channel.IsTransmitting)
            return;
        if (!channel.CanTransmit)
        {
            TransmitStatusText = $"PTT unavailable for {channel.Name}: the channel is RX-only or its encryption key is unavailable.";
            return;
        }
        await StartTransmitAsync(channel).ConfigureAwait(false);
    }

    public async Task StopChannelTransmitAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.IsTransmitting)
            return;
        await StopTransmitAsync(channel).ConfigureAwait(false);
    }

    public bool HandleKeyboardPttDown(KeyboardPttKey key)
    {
        return keyboardPtt.HandleKeyDown(key);
    }

    public bool HandleKeyboardPttUp(KeyboardPttKey key)
    {
        return keyboardPtt.HandleKeyUp(key);
    }

    public bool IsConfiguredPttKey(KeyboardPttKey key) => keyboardPtt.ActivationKey == key;

    public static MainWindowViewModel Load(string? configurationPath)
        => Load(configurationPath, new UserSettingsStore(UserSettingsStore.DefaultPath));

    internal static MainWindowViewModel Load(
        string? configurationPath,
        UserSettingsStore userSettingsStore,
        Func<IReadOnlyList<string>>? serialPortProvider = null,
        Func<string, int, IPttSource>? serialPttFactory = null)
    {
        ArgumentNullException.ThrowIfNull(userSettingsStore);
        if (string.IsNullOrWhiteSpace(configurationPath))
            configurationPath = userSettingsStore.Load().LastCodeplugPath;

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new MainWindowViewModel(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                [],
                [],
                userSettingsStore: userSettingsStore,
                groupDefinitions: [],
                serialPortProvider: serialPortProvider,
                serialPttFactory: serialPttFactory);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            (P25KeyRing p25KeyRing, DmrKeyRing dmrKeyRing, NxdnKeyRing nxdnKeyRing) = LoadKeyRings(
                configuration,
                out string? keyWarning);
            IReadOnlyList<ZoneViewModel> zones = configuration.Zones.Select(zone => new ZoneViewModel(
                zone.Name,
                zone.Channels.Select(channel => new ChannelViewModel(
                    channel,
                    p25KeyRing,
                    configuration.Systems
                        .FirstOrDefault(system => system.Name.Equals(channel.System, StringComparison.OrdinalIgnoreCase))
                        ?.RidAlias,
                    dmrKeyRing,
                    nxdnKeyRing)).ToArray(),
                zone.WebStreams.Select(stream => new WebStreamViewModel(stream)).ToArray(),
                zone.TabColor,
                zone.TabTextColor)).ToArray();
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). Connections are idle until Connect is pressed."
                : $"Configuration has {errors.Count} validation error(s):\n• {string.Join("\n• ", errors)}";
            if (!string.IsNullOrWhiteSpace(keyWarning))
                status = $"{status}\n{keyWarning}";

            var viewModel = new MainWindowViewModel(
                status,
                errors.Count == 0
                    ? CreateSystemViewModels(configuration, zones)
                    : [],
                zones,
                p25KeyRing,
                userSettingsStore,
                configuration.EffectiveGroups(),
                configuration.PatchSourceIdPassthrough,
                serialPortProvider,
                serialPttFactory,
                dmrKeyRing,
                nxdnKeyRing);
            if (errors.Count == 0)
                viewModel.RecordLoadedCodeplug(configuration.SourcePath ?? Path.GetFullPath(configurationPath));
            return viewModel;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            return new MainWindowViewModel(
                $"Unable to load codeplug: {exception.Message}",
                [],
                [],
                userSettingsStore: userSettingsStore,
                groupDefinitions: [],
                serialPortProvider: serialPortProvider,
                serialPttFactory: serialPttFactory);
        }
    }

    private static (P25KeyRing P25, DmrKeyRing Dmr, NxdnKeyRing Nxdn) LoadKeyRings(
        ConsoleConfiguration configuration,
        out string? warning)
    {
        var p25Ring = new P25KeyRing();
        var dmrRing = new DmrKeyRing();
        var nxdnRing = new NxdnKeyRing();
        warning = null;
        if (string.IsNullOrWhiteSpace(configuration.KeyFile))
            return (p25Ring, dmrRing, nxdnRing);

        try
        {
            KeyContainer localKeys = KeyFileLoader.Load(
                ConfigurationLoader.ResolvePath(configuration, configuration.KeyFile));
            foreach (SystemConfiguration system in configuration.Systems)
            {
                p25Ring.AddLocalKeys(system.Name, localKeys);
                dmrRing.AddLocalKeys(system.Name, localKeys);
                nxdnRing.AddLocalKeys(system.Name, localKeys);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            warning = $"Encryption keys unavailable: {exception.Message} Encrypted P25 channels are disabled until FNE/KMM supplies their keys. Encrypted DMR and NXDN channels require local keys.";
            p25Ring.Dispose();
            dmrRing.Dispose();
            nxdnRing.Dispose();
            return (new P25KeyRing(), new DmrKeyRing(), new NxdnKeyRing());
        }
        return (p25Ring, dmrRing, nxdnRing);
    }

    private static IReadOnlyList<SystemViewModel> CreateSystemViewModels(
        ConsoleConfiguration configuration,
        IReadOnlyList<ZoneViewModel> zones)
    {
        var channelsBySystem = new Dictionary<string, List<ChannelViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelViewModel channel in zones.SelectMany(zone => zone.Channels))
        {
            if (!channelsBySystem.TryGetValue(channel.Definition.SystemName, out List<ChannelViewModel>? channels))
            {
                channels = [];
                channelsBySystem.Add(channel.Definition.SystemName, channels);
            }

            channels.Add(channel);
        }

        return configuration.Systems.Select((system, systemIndex) =>
        {
            IBrush systemAccent = SystemAccentPalette.GetBrush(systemIndex);
            IReadOnlyList<ZoneViewModel> systemZones = zones
                .Select(zone => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Where(channel => channel.Definition.SystemName.Equals(
                        system.Name,
                        StringComparison.OrdinalIgnoreCase)).ToArray(),
                    zone.WebStreams,
                    zone.TabColor,
                    zone.TabTextColor,
                    systemAccent))
                .Where(zone => zone.Channels.Count > 0)
                .ToArray();

            return new SystemViewModel(
                FneConnectionOptions.FromConfiguration(system),
                system.Name,
                $"{system.Address}:{system.Port}",
                channelsBySystem.TryGetValue(system.Name, out List<ChannelViewModel>? channels)
                    ? channels
                    : [],
                systemZones,
                systemIndex);
        }).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        clockTimer.Stop();
        clockTimer.Tick -= HandleClockTick;
        audioMeterTimer.Stop();
        audioMeterTimer.Tick -= HandleAudioMeterTick;
        await defaultAudioDeviceMonitor.DisposeAsync().ConfigureAwait(false);
        Task recordingScan;
        CancellationTokenSource? recordingScanCancellation;
        lock (recordingCatalogScanSync)
        {
            recordingScanCancellation = recordingCatalogScanCancellation;
            recordingScanCancellation?.Cancel();
            recordingCatalogScanCancellation = null;
            recordingScan = recordingCatalogScanTask;
        }
        await recordingScan.ConfigureAwait(false);
        recordingScanCancellation?.Dispose();
        transmitCoordinator.Faulted -= HandleTransmitFaulted;
        keyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        if (globalKeyboardPtt is not null)
            globalKeyboardPtt.StateChanged -= HandleKeyboardPttStateChanged;
        await keyboardPtt.DisposeAsync().ConfigureAwait(false);
        if (globalKeyboardPtt is not null)
            await globalKeyboardPtt.DisposeAsync().ConfigureAwait(false);
        await serialPttChangeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            IPttSource? currentSerialPtt = serialPtt;
            serialPtt = null;
            if (currentSerialPtt is not null)
                await StopAndDisposeSerialPttAsync(currentSerialPtt).ConfigureAwait(false);
        }
        finally
        {
            serialPttChangeLock.Release();
        }
        await toneTransmitCoordinator.DisposeAsync().ConfigureAwait(false);
        await localTonePlayer.DisposeAsync().ConfigureAwait(false);
        warmMicrophoneReconciler.Reconciled -= HandleWarmMicrophoneReconciled;
        await warmMicrophoneReconciler.WhenIdleAsync().ConfigureAwait(false);
        await transmitCoordinator.DisposeAsync().ConfigureAwait(false);
        foreach (SystemViewModel system in Systems)
        {
            system.PropertyChanged -= HandleSystemPropertyChanged;
            system.KeyResponseReceived -= HandleSystemKeyResponse;
            system.LogReceived -= HandleSystemLog;
            await system.DisposeAsync().ConfigureAwait(false);
        }
        await receiveAudioWork.DisposeAsync().ConfigureAwait(false);
        await DrainPatchSourceWorkAsync().ConfigureAwait(false);
        await patchSourceDecode.DisposeAsync().ConfigureAwait(false);
        patchForwarding.Dispose();
        await audioCoordinator.DisposeAsync().ConfigureAwait(false);
        await webStreamPlayback.DisposeAsync().ConfigureAwait(false);
        await recordingPlayback.DisposeAsync().ConfigureAwait(false);
        audioReconfigurationLock.Dispose();
        callRecordings.RecordingFinalized -= HandleRecordingFinalized;
        await callRecordings.DisposeAsync().ConfigureAwait(false);
        p25KeyRing?.Dispose();
        dmrKeyRing?.Dispose();
        nxdnKeyRing?.Dispose();
        userBackgroundBitmap?.Dispose();
        userBackgroundBitmap = null;
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.TransmitEncryptionChanged -= HandleChannelEncryptionChanged;
            channel.RecordingStateChanged -= HandleChannelRecordingChanged;
            channel.VolumeChanged -= HandleChannelVolumeChanged;
            channel.StereoBalanceChanged -= HandleChannelStereoBalanceChanged;
        }
        foreach (WebStreamViewModel stream in WebStreams)
        {
            stream.VolumeChanged -= HandleWebStreamVolumeChanged;
            stream.PropertyChanged -= HandleWebStreamPropertyChanged;
        }
    }

    private async Task ConnectAsync()
    {
        SetBusy(true);
        StatusText = "Starting FNE connection services...";
        try
        {
            await Task.WhenAll(Systems.Select(system => StartSystemAsync(system)));
            await SyncPatchSourceDecodeAsync().ConfigureAwait(false);
            StatusText = "FNE connection services started; waiting for login acknowledgements.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartSystemAsync(SystemViewModel system)
    {
        try
        {
            await system.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandleSystemStatus(system, new FneConnectionStatus(
                system.Name,
                FneConnectionState.Faulted,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }

    public async Task ToggleSystemConnectionAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!Systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        SelectedSystem = system;
        if (system.IsConnectionActive)
        {
            StatusText = $"Stopping {system.Name}...";
            try
            {
                await system.StopAsync();
                StatusText = $"{system.Name}: disconnected.";
            }
            catch (Exception exception)
            {
                StatusText = $"{system.Name}: disconnect failed — {exception.Message}";
            }
            return;
        }

        StatusText = $"Starting {system.Name}...";
        await StartSystemAsync(system);
        await SyncPatchSourceDecodeAsync();
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        StatusText = "Stopping FNE connection services...";
        try
        {
            await patchSourceDecode.StopAllAsync().ConfigureAwait(false);
            patchForwarding.StopAll();
            await Task.WhenAll(Systems.Select(system => system.StopAsync()));
            StatusText = "FNE connections stopped.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void HandleSystemStatus(SystemViewModel system, FneConnectionStatus status)
    {
        void Apply()
        {
            system.ApplyStatus(status);
            StatusText = $"{system.Name}: {status.State} — {status.Message}";
            NotifyConnectionPresentationChanged();
            if (status.State == FneConnectionState.Connected)
            {
                RequestConfiguredP25Keys(system);
                _ = ReconcileReceiveSessionsAsync();
            }
            bool stateChanged = !lastConnectionStates.TryGetValue(system.Name, out FneConnectionState previousState) ||
                previousState != status.State;
            lastConnectionStates[system.Name] = status.State;
            if (stateChanged &&
                previousState == FneConnectionState.Connected &&
                status.State != FneConnectionState.Connected &&
                p25KeyRing is not null)
            {
                p25KeyRing.ClearFneKeys(system.Name);
                RefreshP25KeyState();
                _ = SyncPatchSourceDecodeAsync();
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
                _ = PlayConnectionChimeAsync(system.Name, status.State);
            RaiseGeneratedAudioCanExecuteChanged();
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
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
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"{systemName} connection chime unavailable: {exception.Message}");
        }
    }

    private void HandleSystemLog(object? sender, FneLogEntry entry)
        => AddDebugLog(entry.Timestamp, entry.SystemName, entry.Severity, entry.Message);

    private void AddDebugLog(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
    {
        void Apply()
        {
            if (debugLogEntries.Count >= 500)
                debugLogEntries.RemoveAt(debugLogEntries.Count - 1);

            debugLogEntries.Insert(0, new DebugLogEntry(
                timestamp,
                source,
                severity,
                DebugLogRedactor.Redact(message)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredDebugLogs)));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void RequestConfiguredP25Keys(SystemViewModel system)
    {
        // Request every configured key even when a local fallback is available.
        // Valid KMM material takes precedence for this system when it arrives.
        if (p25KeyRing is null)
            return;

        foreach ((byte algorithmId, ushort keyId) in ResolveConfiguredP25KeyRequests(system.Channels))
        {
            try
            {
                system.RequestP25Key(algorithmId, keyId);
            }
            catch (Exception exception)
            {
                StatusText = $"{system.Name}: P25 key request unavailable — {exception.Message}";
            }
        }
    }

    internal static IReadOnlyList<(byte AlgorithmId, ushort KeyId)> ResolveConfiguredP25KeyRequests(
        IEnumerable<ChannelViewModel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels
            .Where(channel => channel.Definition.Mode == "p25" && channel.Definition.IsEncrypted)
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
                _ = SyncPatchSourceDecodeAsync();
            }
            catch (ArgumentException exception)
            {
                StatusText = $"{system.Name}: rejected P25 KMM key 0x{response.KeyId:X4} — {exception.Message}";
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
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
            return;
        }

        _ = EnsureRecordingAudioAsync(channel);
    }

    private void HandleChannelVolumeChanged(object? sender, double volume)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.ChannelVolumes[channel.SettingsKey] = volume;
        PersistUserSettings();
        _ = audioCoordinator.SetGainAsync(channel, volume);
    }

    private void HandleChannelStereoBalanceChanged(object? sender, double balance)
    {
        if (sender is not ChannelViewModel channel)
            return;

        userSettings.ChannelStereoBalances[channel.SettingsKey] = balance;
        PersistUserSettings();
        _ = audioCoordinator.SetBalanceAsync(channel, balance);
    }

    private async Task StartWebStreamAsync(WebStreamViewModel stream)
    {
        try
        {
            await webStreamPlayback.StartAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            AudioStatusText = stream.IsFailed
                ? $"Web stream {stream.Name}: {stream.StatusText}"
                : $"Web stream {stream.Name}: {stream.StatusText}";
        }
        catch (OperationCanceledException)
        {
            stream.SetPlaybackState(false, false, false, false, "Off");
        }
        catch (Exception exception)
        {
            stream.SetPlaybackState(false, false, false, true, $"Failed: {exception.Message}");
            AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
        }
    }

    private async Task StopWebStreamAsync(WebStreamViewModel stream)
    {
        try
        {
            await webStreamPlayback.StopAsync(stream).ConfigureAwait(false);
            PersistSelectedWebStreamState(stream);
            AudioStatusText = $"Web stream {stream.Name}: Off";
        }
        catch (OperationCanceledException)
        {
            stream.SetPlaybackState(false, false, false, false, "Off");
        }
        catch (Exception exception)
        {
            stream.SetPlaybackState(false, false, false, true, $"Failed to stop: {exception.Message}");
            AudioStatusText = $"Web stream {stream.Name}: {stream.StatusText}";
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

        HashSet<string> selectedNames = userSettings.SelectedWebStreams
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (WebStreamViewModel stream in webStreams.Where(stream => selectedNames.Contains(stream.Name)))
            await StartWebStreamAsync(stream).ConfigureAwait(false);
    }

    private void PersistSelectedWebStreamState(WebStreamViewModel stream)
    {
        if (!userSettings.RestoreSelectedChannelsOnStartup)
            return;

        HashSet<string> selectedNames = userSettings.SelectedWebStreams
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stream.IsActive && !stream.IsFailed)
            selectedNames.Add(stream.Name);
        else
            selectedNames.Remove(stream.Name);
        userSettings.SelectedWebStreams = selectedNames.ToList();
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

    // Receive uses plain CoreAudio even when Apple voice processing is selected
    // for the microphone. This prevents platform AEC/AGC from altering decoded
    // radio audio or the operator's output level.
    private IAudioBackend CreateReceiveAudioBackend()
        => AudioBackendFactory.CreateDefault(
            Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));

    // The selected processing mode is intentionally scoped to microphone
    // capture for transmit. ProcessedAudioCapture further confines the
    // DVM Console gain/EQ/AGC path to this capture stream.
    private IAudioBackend CreateTransmitAudioBackend()
        => AudioBackendFactory.CreateDefault(
            Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"),
            GetConfiguredAudioProcessingMode(),
            userSettings.AudioInputDeviceId,
            userSettings.AudioOutputDeviceId,
            userSettings.HighQualityBluetoothAudioEnabled);

    private void HandleHighQualityBluetoothStatusChanged(
        object? sender,
        HighQualityBluetoothAudioStatus status)
    {
        if (!IsHighQualityBluetoothAudioAvailable || !userSettings.HighQualityBluetoothAudioEnabled)
            return;
        string? message = status switch
        {
            HighQualityBluetoothAudioStatus.Active =>
                "High-quality AirPods input and output are active at full bandwidth.",
            HighQualityBluetoothAudioStatus.Requested =>
                "High-quality AirPods audio was requested; macOS is still confirming the route.",
            HighQualityBluetoothAudioStatus.Unsupported =>
                "The selected Bluetooth route does not support high-quality recording; normal Bluetooth audio is active.",
            HighQualityBluetoothAudioStatus.Unavailable when userSettings.HighQualityBluetoothAudioEnabled =>
                "High-quality AirPods audio is unavailable for the current route; normal CoreAudio is active.",
            _ => null
        };
        if (message is not null)
            Dispatcher.UIThread.Post(() => AudioStatusText = message);
    }

    private void HandleWarmMicrophoneReconciled(object? sender, LatestBooleanStateResult result)
    {
        Dispatcher.UIThread.Post(() =>
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
        => OperatingSystem.IsMacOS() &&
           userSettings.AudioProcessingMode == UserSettings.AppleVoiceProcessingMode
            ? AudioProcessingMode.AppleVoiceProcessing
            : AudioProcessingMode.DvmConsole;

    private AudioProcessingMode GetSelectedAudioProcessingMode()
        => SelectedAudioProcessingMode == AppleVoiceProcessingDisplay
            ? AudioProcessingMode.AppleVoiceProcessing
            : AudioProcessingMode.DvmConsole;

    private static string ToAudioProcessingModeDisplay(string? mode)
        => OperatingSystem.IsMacOS() && mode == UserSettings.AppleVoiceProcessingMode
            ? AppleVoiceProcessingDisplay
            : DvmConsoleProcessingDisplay;

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
        if (!audioCoordinator.IsActive(channel))
            await StartAudioAsync(channel);
    }

    private void HandleDecodedSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        patchForwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples);
        callRecordings.WriteSamples(channel, streamId, sourceId, samples);
        audioMeterPipeline.Observe(channel, streamId, samples.Span, ChannelAudioDirection.Receive);
        LogVocoderAudioLevel(channel, samples, ChannelAudioDirection.Receive, streamId);
    }

    private void HandleTransmitSamples(
        ChannelViewModel channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        callRecordings.WriteTransmitSamples(channel, streamId, sourceId, samples);
        audioMeterPipeline.Observe(channel, streamId, samples.Span, ChannelAudioDirection.Transmit);
        LogVocoderAudioLevel(channel, samples, ChannelAudioDirection.Transmit, streamId);
    }

    private void LogVocoderAudioLevel(
        ChannelViewModel channel,
        ReadOnlyMemory<short> samples,
        ChannelAudioDirection direction,
        uint streamId = 0)
    {
        if (samples.IsEmpty)
            return;

        DateTimeOffset now = DateTimeOffset.Now;
        lock (audioLevelLogSync)
        {
            var key = (channel, direction);
            if (lastAudioLevelLogs.TryGetValue(key, out DateTimeOffset previous) &&
                now - previous < TimeSpan.FromSeconds(1))
            {
                return;
            }
            lastAudioLevelLogs[key] = now;
        }

        double squares = 0;
        int peak = 0;
        foreach (short sample in samples.Span)
        {
            double value = sample;
            squares += value * value;
            peak = Math.Max(peak, Math.Abs((int)sample));
        }
        double rms = Math.Sqrt(squares / samples.Length);
        double rmsDbfs = 20 * Math.Log10(Math.Max(rms / 32768.0, 1e-9));
        double peakDbfs = 20 * Math.Log10(Math.Max(peak / 32768.0, 1e-9));
        string streamText = streamId == 0 ? string.Empty : $", stream {streamId}";
        AddDebugLog(
            now,
            channel.Definition.SystemName,
            DebugLogSeverity.Debug,
            $"Vocoder {direction.ToString().ToUpperInvariant()} {ProtocolFor(channel).ToString().ToUpperInvariant()} " +
            $"on {channel.Name}: PCM RMS {rmsDbfs:0.0} dBFS, peak {peakDbfs:0.0} dBFS, " +
            $"{samples.Length} samples{streamText}.");
    }

    private void HandleAudioMeterTick(object? sender, EventArgs e)
    {
        foreach (ChannelAudioMeterUpdate update in audioMeterPipeline.Advance())
            update.Channel.SetAudioLevel(update.Level, update.Direction, update.StreamId);
    }

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
            ChannelViewModel[] channels = PatchGroups
                .Where(group => group.IsEnabled)
                .SelectMany(group => group.Members
                    .Where(member => member.IsMember)
                    .Select(member => member.Channel))
                .Distinct()
                .ToArray();
            await patchSourceDecode.ApplyChannelsAsync(channels).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"Patch source decode unavailable: {exception.Message}");
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
            Dispatcher.UIThread.Post(() =>
                AudioStatusText = $"Patch source decode stopped: {exception.Message}");
        }
    }

    private void EnqueuePatchSource(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        Task current;
        lock (patchSourceWorkSync)
        {
            Task previous = patchSourceWork.TryGetValue(channel, out Task? pending)
                ? pending
                : Task.CompletedTask;
            current = previous
                .ContinueWith(
                    _ => ProcessPatchSourceAsync(channel, traffic),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            patchSourceWork[channel] = current;
        }

        _ = current.ContinueWith(
            _ =>
            {
                lock (patchSourceWorkSync)
                {
                    if (patchSourceWork.TryGetValue(channel, out Task? pending) &&
                        ReferenceEquals(pending, current))
                    {
                        patchSourceWork.Remove(channel);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        Dispatcher.UIThread.Post(() =>
        {
            channel.SetRecordingEnabled(false);
            AudioStatusText = $"TAR recording stopped: {exception.Message}";
        });
    }

    private void HandleRecordingFinalized(object? sender, RecordingFinalizationResult result)
    {
        Dispatcher.UIThread.Post(() =>
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

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
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
        Dispatcher.UIThread.Post(() =>
            AudioStatusText = $"Recording playback stopped: {exception.Message}");
    }

    private void RefreshRecordings(bool pruneExpired = false)
    {
        CancellationTokenSource cancellation = new();
        int generation;
        long mutationRevision;
        lock (recordingCatalogScanSync)
        {
            recordingCatalogScanCancellation?.Cancel();
            recordingCatalogScanCancellation?.Dispose();
            recordingCatalogScanCancellation = cancellation;
            generation = ++recordingCatalogScanGeneration;
            mutationRevision = recordingCatalogMutationRevision;
        }
        Task scan = RefreshRecordingsAsync(generation, mutationRevision, pruneExpired, cancellation.Token);
        lock (recordingCatalogScanSync)
        {
            if (generation == recordingCatalogScanGeneration)
                recordingCatalogScanTask = scan;
        }
    }

    private async Task RefreshRecordingsAsync(
        int generation,
        long mutationRevision,
        bool pruneExpired,
        CancellationToken cancellationToken)
    {
        try
        {
            if (pruneExpired)
                await Task.Run(() => callRecordings.PruneExpired(), cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CallRecordingMetadata> loaded = await callRecordings
                .LoadRecordingsAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            bool applied = await ApplyRecordingCatalogAsync(
                loaded,
                generation,
                mutationRevision,
                cancellationToken).ConfigureAwait(false);
            if (!applied && !cancellationToken.IsCancellationRequested)
            {
                bool restart;
                lock (recordingCatalogScanSync)
                {
                    restart = generation == recordingCatalogScanGeneration &&
                        mutationRevision != recordingCatalogMutationRevision;
                }
                if (restart)
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
        int generation,
        long mutationRevision,
        CancellationToken cancellationToken)
    {
        var desiredIds = new HashSet<string>(loaded.Select(RecordingCatalogKey), StringComparer.OrdinalIgnoreCase);
        CallRecordingMetadata[] existing = [];
        if (!await ApplyRecordingCatalogUiBatchAsync(
                generation,
                mutationRevision,
                cancellationToken,
                () => existing = recordingEntries.ToArray()).ConfigureAwait(false))
        {
            return false;
        }

        string[] removedKeys = existing
            .Select(RecordingCatalogKey)
            .Where(key => !desiredIds.Contains(key))
            .ToArray();
        foreach (string[] batch in removedKeys.Chunk(RecordingCatalogUiBatchSize))
        {
            if (!await ApplyRecordingCatalogUiBatchAsync(generation, mutationRevision, cancellationToken, () =>
                {
                    var keys = new HashSet<string>(batch, StringComparer.OrdinalIgnoreCase);
                    for (int index = recordingEntries.Count - 1; index >= 0; index--)
                    {
                        if (keys.Contains(RecordingCatalogKey(recordingEntries[index])))
                            recordingEntries.RemoveAt(index);
                    }
                    callHistory.RemoveRecordingsByKey(keys);
                }).ConfigureAwait(false))
            {
                return false;
            }
        }

        for (int batchStart = 0; batchStart < loaded.Count; batchStart += RecordingCatalogUiBatchSize)
        {
            int startIndex = batchStart;
            CallRecordingMetadata[] batch = loaded
                .Skip(batchStart)
                .Take(RecordingCatalogUiBatchSize)
                .ToArray();
            if (!await ApplyRecordingCatalogUiBatchAsync(generation, mutationRevision, cancellationToken, () =>
            {
                for (int offset = 0; offset < batch.Length; offset++)
                {
                    int desiredIndex = startIndex + offset;
                    CallRecordingMetadata metadata = batch[offset];
                    string key = RecordingCatalogKey(metadata);
                    if (desiredIndex < recordingEntries.Count && RecordingCatalogKey(recordingEntries[desiredIndex])
                            .Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        recordingEntries[desiredIndex] = metadata;
                        continue;
                    }

                    int existingIndex = -1;
                    for (int candidate = desiredIndex + 1; candidate < recordingEntries.Count; candidate++)
                    {
                        if (!RecordingCatalogKey(recordingEntries[candidate])
                                .Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        existingIndex = candidate;
                        break;
                    }

                    if (existingIndex < 0)
                    {
                        recordingEntries.Insert(Math.Min(desiredIndex, recordingEntries.Count), metadata);
                        continue;
                    }

                    recordingEntries[existingIndex] = metadata;
                    if (existingIndex != desiredIndex)
                        recordingEntries.Move(existingIndex, desiredIndex);
                }
                callHistory.AddOrAttachRecordings(batch);
            }).ConfigureAwait(false))
            {
                return false;
            }
        }

        return await ApplyRecordingCatalogUiBatchAsync(generation, mutationRevision, cancellationToken, () =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecordings)));
            NotifyCallHistoryChanged();
        }).ConfigureAwait(false);
    }

    private async Task<bool> ApplyRecordingCatalogUiBatchAsync(
        int generation,
        long mutationRevision,
        CancellationToken cancellationToken,
        Action action)
    {
        bool applied = false;
        await RunOnUiThreadAsync(() =>
        {
            lock (recordingCatalogScanSync)
            {
                if (!IsRecordingCatalogSnapshotCurrent(
                        generation,
                        recordingCatalogScanGeneration,
                        mutationRevision,
                        recordingCatalogMutationRevision,
                        cancellationToken.IsCancellationRequested))
                    return;
                action();
                applied = true;
            }
        }).ConfigureAwait(false);
        return applied;
    }

    private void RecordRecordingCatalogMutation()
    {
        lock (recordingCatalogScanSync)
            recordingCatalogMutationRevision++;
    }

    internal static bool IsRecordingCatalogSnapshotCurrent(
        int snapshotGeneration,
        int currentGeneration,
        long snapshotMutationRevision,
        long currentMutationRevision,
        bool isCancellationRequested)
        => !isCancellationRequested &&
           snapshotGeneration == currentGeneration &&
           snapshotMutationRevision == currentMutationRevision;

    private static string RecordingCatalogKey(CallRecordingMetadata metadata)
        => !string.IsNullOrWhiteSpace(metadata.RecordingId) ? metadata.RecordingId : metadata.FilePath;


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


    private void HandleKeyboardPttStateChanged(object? sender, bool pressed)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _ = HandleKeyboardPttStateChangedAsync(pressed);
        else
            Dispatcher.UIThread.Post(() => _ = HandleKeyboardPttStateChangedAsync(pressed));
    }

    private async Task HandleKeyboardPttStateChangedAsync(bool pressed)
    {
        if (pressed)
        {
            ChannelViewModel[] targets = Systems
                .SelectMany(system => system.Channels)
                .Where(channel => channel.IsTransmitSelected)
                .ToArray();
            if (targets.Length == 0)
            {
                TransmitStatusText = $"Choose TX on one or more cards before using {GlobalPttKeyText}.";
                return;
            }
            if (transmitCoordinator.ActiveChannel is not null)
                return;

            await StartTransmitAsync(targets);
            if (!AnyPttSourcePressed && transmitCoordinator.ActiveChannel is not null)
                await StopTransmitAsync(transmitCoordinator.ActiveChannels);
            return;
        }

        if (AnyPttSourcePressed)
            return;

        ChannelViewModel[] active = transmitCoordinator.ActiveChannels.ToArray();
        if (active.Length > 0)
            await StopTransmitAsync(active);
    }

    private bool AnyPttSourcePressed
        => (globalKeyboardPtt?.IsPressed ?? keyboardPtt.IsPressed) || serialPtt?.IsPressed == true;

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
        ChannelViewModel[] channels = transmitCoordinator.ActiveChannels.ToArray();
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (channel, transmitCoordinator.GetActiveStreamId(channel)))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (ChannelViewModel channel in channels)
                channel.SetTransmitEnabled(false);
            activeMultiSelectGroup?.SetPttActive(false);
            activeMultiSelectGroup = null;
            TransmitStatusText = $"Transmission stopped: {exception.Message}";
        });
        _ = Task.Run(async () =>
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
                    Dispatcher.UIThread.Post(NotifyCallHistoryChanged);
            }
            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                Dispatcher.UIThread.Post(() =>
                    TransmitStatusText = $"Transmission stopped; audio recovery failed: {cleanupException.Message}");
            }
        });
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
        ExpireStaleReceiveStates(DateTimeOffset.UtcNow);
        _ = ReconcileReceiveSessionsAsync();
    }

    internal void ExpireStaleReceiveStates(DateTimeOffset now)
    {
        bool callHistoryChanged = false;
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            ChannelTrafficApplyResult applied = channel.AdvanceReceiveLifecycle(now);
            if (applied.Transition != ReceiveStreamTransition.GraceExpired ||
                applied.EndedStreamId is not uint streamId)
            {
                continue;
            }

            callHistoryChanged = callHistory.Complete(
                channel.Definition.SystemName,
                channel.Definition.Mode switch
                {
                    "dmr" => FneTrafficProtocol.Dmr,
                    "p25" => FneTrafficProtocol.P25,
                    "nxdn" => FneTrafficProtocol.Nxdn,
                    _ => FneTrafficProtocol.Analog
                },
                streamId,
                now,
                channel.Name,
                channel.Definition.DestinationId) || callHistoryChanged;
            callRecordings.StopChannel(channel);
        }
        if (callHistoryChanged)
            NotifyCallHistoryChanged();
    }

    private void NotifyCallHistoryChanged()
    {
        RefreshFilteredCallHistory();
        RefreshActivityCallHistory();
    }

    private void RefreshFilteredCallHistory()
        => SynchronizeHistoryView(filteredCallHistoryEntries, CallHistory.Where(CreateHistoryFilter().Matches));

    private void RefreshActivityCallHistory()
    {
        CallHistoryEntry[] desired = SelectActivityHistory(
            CallHistory,
            SelectedSystem?.Name,
            activityCurrentZoneOnly
                ? SelectedSystem?.SelectedZone?.Channels.Select(channel => channel.Name)
                : null);
        SynchronizeHistoryView(activityCallHistoryEntries, desired);
    }

    internal static CallHistoryEntry[] SelectActivityHistory(
        IEnumerable<CallHistoryEntry> history,
        string? selectedSystemName,
        IEnumerable<string>? selectedZoneChannelNames)
    {
        if (selectedSystemName is null)
            return [];

        HashSet<string>? selectedChannels = selectedZoneChannelNames is null
            ? null
            : new HashSet<string>(selectedZoneChannelNames, StringComparer.OrdinalIgnoreCase);
        return history
            .Where(entry => entry.SystemName.Equals(selectedSystemName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => selectedChannels is null || selectedChannels.Contains(entry.ChannelName))
            .Take(CallHistoryStore.DefaultMaxEntries)
            .ToArray();
    }

    internal static void SynchronizeHistoryView(
        ObservableCollection<CallHistoryEntry> target,
        IEnumerable<CallHistoryEntry> desiredEntries)
    {
        CallHistoryEntry[] desired = desiredEntries.ToArray();
        var desiredSet = new HashSet<CallHistoryEntry>(desired, ReferenceEqualityComparer.Instance);
        lock (target)
        {
            for (int index = target.Count - 1; index >= 0; index--)
            {
                if (!desiredSet.Contains(target[index]))
                    target.RemoveAt(index);
            }

            for (int index = 0; index < desired.Length; index++)
            {
                if (index < target.Count && ReferenceEquals(target[index], desired[index]))
                    continue;
                int existingIndex = target.IndexOf(desired[index]);
                if (existingIndex >= 0)
                    target.Move(existingIndex, index);
                else
                    target.Insert(index, desired[index]);
            }
        }
    }

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
            userSettingsStore.Save(userSettings);
        }
        catch (IOException)
        {
            // Operator state must never prevent the console from running.
        }
    }

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
            .Select(member => new PatchMemberAddress(member.SystemName.Trim(), member.DestinationId))
            .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        PersistGroupDefinition(normalizedName, normalizedMembers, enabled, oneWay);
        ReapplyPatchState();
        PersistUserSettings();
        RefreshPatchMembershipConflicts();
        _ = SyncPatchSourceDecodeAsync();
    }

    public void ApplyPatchGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<PatchMemberAddress> members = group.Members
            .Where(member => member.IsMember)
            .Select(member => new PatchMemberAddress(
                member.Channel.Definition.SystemName,
                member.Channel.Definition.DestinationId))
            .ToList();
        if (group.IsMultiSelect)
        {
            if (ReferenceEquals(activeMultiSelectGroup, group))
            {
                StatusText = $"Stop multi-select PTT for '{group.Name}' before changing its membership.";
                return;
            }
            PersistGroupDefinition(group.Name, members, enabled: true, oneWay: false);
            PersistUserSettings();
            RefreshPatchMembershipConflicts();
            StatusText = $"Multi-select group '{group.Name}' saved with {members.Count} member(s).";
            return;
        }

        ApplyPatchGroup(group.Name, members, group.IsEnabled, group.IsOneWay);
        RefreshPatchMembershipConflicts();
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
        userSettings.PatchGroupMemberships[normalizedName] = members
            .Select(member => new PatchMemberSetting
            {
                SystemName = member.SystemName,
                DestinationId = member.DestinationId
            })
            .ToList();
        userSettings.PatchGroupModes[normalizedName] = oneWay;
        userSettings.PatchGroupEnabledStates[normalizedName] = enabled;
    }

    private IReadOnlyList<PatchGroupEditorViewModel> BuildPatchGroups(
        IEnumerable<GroupConfiguration> groupDefinitions)
    {
        IReadOnlyList<ChannelViewModel> channels = Systems
            .SelectMany(system => system.Channels)
            .ToArray();
        List<PatchGroupEditorViewModel> groups = [];
        foreach (GroupConfiguration definition in groupDefinitions)
        {
            string groupName = definition.Name.Trim();
            if (groupName.Length == 0)
                continue;

            HashSet<string> configuredMembers = userSettings.PatchGroupMemberships
                .TryGetValue(groupName, out List<PatchMemberSetting>? savedMembers)
                ? (savedMembers ?? [])
                    .Select(member => $"{member.SystemName.Trim().ToLowerInvariant()}|{member.DestinationId}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            bool isMultiSelect = definition.IsMultiselectGroup();
            bool enabled = isMultiSelect ||
                (userSettings.PatchGroupEnabledStates.TryGetValue(groupName, out bool savedEnabled) && savedEnabled);
            bool oneWay = userSettings.PatchGroupModes.TryGetValue(groupName, out bool savedOneWay) && savedOneWay;
            var group = new PatchGroupEditorViewModel(
                groupName,
                enabled,
                oneWay,
                channels.Select(channel => new PatchMemberEditorViewModel(
                    channel,
                    configuredMembers.Contains($"{channel.Definition.SystemName.ToLowerInvariant()}|{channel.Definition.DestinationId}"))),
                isMultiSelect);
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
        var memberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> entry in userSettings.PatchGroupMemberships)
        {
            if (!patchGroupNames.Contains(entry.Key))
                continue;
            if (!userSettings.PatchGroupEnabledStates.TryGetValue(entry.Key, out bool enabled) || !enabled)
                continue;

            memberships[entry.Key] = entry.Value
                .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
                .Select(member => new PatchMemberAddress(member.SystemName.Trim(), member.DestinationId))
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        patchForwarding.ApplyMemberships(memberships, userSettings.PatchGroupModes);
    }

    private void RecordLoadedCodeplug(string path)
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

    private static string GetDefaultRecordingRoot(string? configuredRootPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRootPath))
            return Path.GetFullPath(configuredRootPath.Trim());

        string settingsPath = UserSettingsStore.DefaultPath;
        string? settingsDirectory = Path.GetDirectoryName(settingsPath);
        return Path.Combine(settingsDirectory ?? AppContext.BaseDirectory, "Recordings");
    }
}
