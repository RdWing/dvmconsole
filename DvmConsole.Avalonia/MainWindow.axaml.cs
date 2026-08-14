// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Audio;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Hotkeys;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Networking;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;
using fnecore;

namespace DvmConsole.Avalonia
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Optional physical key-state probe driving the PTT key-up
        /// watchdog timer; null keeps the watchdog dormant. Set only by
        /// the three-dependency constructor.
        /// </summary>
        private readonly IKeyboardKeyStateReader? keyStateReader = null;

        /// <summary>
        /// The single 250 ms PTT key-up watchdog timer; null while no
        /// key-state reader was supplied or after the window closes.
        /// </summary>
        private DispatcherTimer? watchdogTimer = null;

        /// <summary>
        /// Hourly TAR retention maintenance timer; null when recording is
        /// not composed or after the window closes.
        /// </summary>
        private DispatcherTimer? tarRetentionTimer = null;

        /// <summary>
        /// Headless FNE connection service composed by this window
        /// together with its bridge; null until the view model is
        /// created in the full-arity constructor. The systems list from
        /// the constructor seeds the service's rows; a null or empty
        /// list keeps the slice dormant: zero rows, no transports, no
        /// events.
        /// </summary>
        private IFneConnectionService? fneConnectionService = null;

        /// <summary>
        /// The bridge forwarding the FNE connection manager's requests
        /// into <see cref="fneConnectionService"/> and marshalling
        /// service state back onto the UI thread; null until composed.
        /// </summary>
        private FneConnectionServiceBridge? fneConnectionBridge = null;

        /// <summary>
        /// The fnecore transport factory backing the FNE slice; null
        /// until the full-arity constructor composes it (from the
        /// injected factory or a fresh instance). The factory registry
        /// is shared with the voice traffic sender composed by
        /// <see cref="App"/> and its <see cref="FnecoreTransportFactory.OnCreated"/>
        /// hook subscribes the receive glue to every adapter.
        /// </summary>
        private FnecoreTransportFactory? fnecoreTransportFactory = null;

        /// <summary>
        /// Routes received FNE voice frames into the talkgroup audio
        /// router; null while no audio router is composed or after the
        /// window closes. Frames arriving after close are dropped by
        /// dropped by the glue's disposed state.
        /// </summary>
        private FneReceiveGlue? fneReceiveGlue = null;

        /// <summary>
        /// Composes Core PatchManager with classified receive metadata and
        /// decoded PCM. The coordinator owns only receive-forward state;
        /// null keeps the runtime patch path dormant when no codeplug was
        /// loaded.
        /// </summary>
        private readonly PatchForwardingCoordinator? patchForwardingCoordinator;

        /// <summary>
        /// Owns the separate patch/multi-select PTT request lifecycle. This
        /// state is deliberately not shared with receive-side patch
        /// forwarding; both paths may use the same audio router, but their
        /// target snapshots and stream lifecycles are independent.
        /// </summary>
        private PatchPttRuntimeCoordinator? patchPttRuntimeCoordinator;

        /// <summary>
        /// Owns the independent momentary PTT lifecycle for one channel card.
        /// It is separate from dashboard/all-channel PTT and patch-group PTT;
        /// all three paths share the router's single capture through explicit
        /// collision guards.
        /// </summary>
        private ChannelPttRuntimeCoordinator? channelPttRuntimeCoordinator;

        /// <summary>
        /// Dashboard PTT remains active until the router's awaited end
        /// completes. This is separate from the view-model engagement flag,
        /// which changes at the UI release edge before audio teardown.
        /// </summary>
        private int dashboardTransmitActive;

        /// <summary>
        /// Suppresses the synthetic dashboard release emitted while a normal
        /// PTT gesture is rejected because a patch capture is active.
        /// </summary>
        private bool rejectDashboardPttRelease;

        /// <summary>
        /// Tracks classified receive state independently of the receive
        /// thread and projects it onto the current channel slot instances.
        /// </summary>
        private ReceiveProjection? receiveProjection = null;

        /// <summary>One-second WPF-parity receive idle sweep timer.</summary>
        private DispatcherTimer? receiveProjectionTimer = null;

        /// <summary>View-model subscribed for zone rebuild projection.</summary>
        private MainWindowViewModel? receiveProjectionViewModel = null;

        /// <summary>
        /// Headless talkgroup audio router composed when an audio stream
        /// factory is supplied to the full-arity constructor; null keeps
        /// the audio slice dormant (headless tests are unaffected). The
        /// router owns the shared factory and is disposed on window
        /// close. Composed with the null codec pair and the stub traffic
        /// sender until the Platform-native vocoder adapter and the
        /// fnecore traffic adapter land (follow-on slices).
        /// </summary>
        private TalkgroupAudioRouter? talkgroupAudioRouter = null;

        /// <summary>
        /// Shell-owned generated/file tone dispatch lifecycle. The coordinator
        /// snapshots targets and applies collision/availability guards before
        /// handing PCM to the router.
        /// </summary>
        private ToneDispatchRuntimeCoordinator? toneDispatchCoordinator;

        /// <summary>Concrete macOS catalog subscribed for capture recovery.</summary>
        private MacAudioDeviceCatalog? macAudioDeviceCatalog = null;

        /// <summary>
        /// Resolves the primary channel's codeplug channel name onto the
        /// router's <see cref="TransmitTarget"/>; null until a codeplug
        /// is supplied to the full-arity constructor, keeping the PTT
        /// path a documented no-op.
        /// </summary>
        private readonly TransmitTargetResolver? transmitTargetResolver = null;

        private readonly TarRecorder? tarRecorder;
        private readonly TarRecordingCoordinator? tarRecordingCoordinator;
        private readonly IAudioWaveFilePlayer? tarWaveFilePlayer;
        private readonly TarViewerColumnSettingsPersistence? tarViewerColumnPersistence;
        private readonly Codeplug? codeplug;
        private GroupSettingsPersistence? groupsPersistence;
        private readonly string groupsMembershipContextKey =
            System.IO.Path.GetFullPath(
                System.IO.Path.Combine(
                    Environment.CurrentDirectory,
                    "configs",
                    "codeplug.yml"));
        private readonly IGlobalHotkeyService? hotkeys;
        private readonly bool ownsRuntimeServices;
        private readonly HotkeyRegistrationCoordinator? hotkeyRegistrationCoordinator;
        private LayoutSettingsPersistence? layoutPersistence;
        private UserSettingsLayoutSection? layoutSection;
        private bool layoutHydrated;
        private TarViewerWindow? tarViewerWindow;
        private PatchGroupsWindow? patchGroupsWindow;
        private AlertToneManagerWindow? alertToneManagerWindow;
        private TonePresetManagerWindow? tonePresetManagerWindow;
        private DtmfPresetManagerWindow? dtmfPresetManagerWindow;
        private QuickCallWindow? quickCallWindow;
        private AlertSettingsPersistence? alertSettingsPersistence;
        private SettingsTransferService? settingsTransferService;
        private SettingsTransferWindow? settingsTransferWindow;
        private DiagnosticLogSink? diagnosticLogSink;
        private DebugLogWindow? debugLogWindow;
        private WidgetSelectionWindow? widgetSelectionWindow;
        private SubscriberCommandService? subscriberCommandService;
        private SubscriberCommandWindow? subscriberCommandWindow;
        private string? userBackgroundPath;
        private Bitmap? userBackgroundBitmap;
        private IAudioWaveFileInspector? alertTonePreviewInspector;
        private IAudioWaveFilePlayer? alertTonePreviewPlayer;
        private readonly AudioSettingsPersistence? audioSettingsPersistence;
        private readonly IAudioStreamFactory? audioStreamFactory;
        private IWebStreamSourceFactory? webStreamSourceFactory;
        private RestoreSettingsPersistence? restoreSettingsPersistence;
        private WebStreamShellViewModel? webStreamShell;
        private WebStreamShellItemViewModel? draggedWebStream;
        private global::Avalonia.Point webStreamDragStart;
        private WebStreamShellPosition webStreamPositionStart;
        private readonly object runtimeDisposeGate = new();
        private Task? runtimeDisposeTask;
        private Func<Codeplug, string, MainWindow>? createCodeplugWindow;
        private Func<MainWindow, MainWindow, Task>? installCodeplugWindow;
        private CodeplugReloadCoordinator? codeplugReloadCoordinator;
        private string codeplugPath = string.Empty;
        private string? pendingCodeplugPath;
        private bool suppressRuntimeSettingsPersistence;
        private bool runtimeActivated;

        public MainWindow()
            : this(null, null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog composed into the view-model; a null catalog leaves
        /// the audio-settings slice absent, and no hotkey service is
        /// composed so the PTT slice is absent too.
        /// </summary>
        public MainWindow(IAudioDeviceCatalog? catalog)
            : this(catalog, null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog and global hotkey service composed into the
        /// view-model; nulls leave the corresponding slices absent.
        /// No audio persistence is composed, so the audio slice is
        /// request-only.
        /// </summary>
        public MainWindow(IAudioDeviceCatalog? catalog, IGlobalHotkeyService? hotkeys)
            : this(catalog, hotkeys, (IKeyboardKeyStateReader?)null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog, global hotkey service and optional physical key-state
        /// reader. When a key-state reader is supplied, the PTT watchdog
        /// is enabled; otherwise it remains dormant.
        /// </summary>
        public MainWindow(
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            IKeyboardKeyStateReader? keyStateReader)
            : this(catalog, hotkeys, keyStateReader, null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog, global hotkey service, optional physical key-state
        /// reader, and optional audio-settings persistence. The persistence
        /// parameter is last so the pre-existing three-argument watchdog
        /// constructor remains source-compatible, including null-literal
        /// calls. When a key-state reader is present, exactly one 250 ms
        /// dispatcher timer polls the PTT hotkey and detaches on close.
        /// When audio settings are present, this window subscribes once to
        /// their property changes and applies selections to the ComboBoxes.
        /// No vocoder readiness result is composed, so the view-model's
        /// <c>VocoderStatus</c> stays null. No registration,
        /// unregistration, or disposal is performed here.
        /// </summary>
        public MainWindow(
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            IKeyboardKeyStateReader? keyStateReader,
            AudioSettingsPersistence? persistence)
            : this(catalog, hotkeys, keyStateReader, persistence, null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog, global hotkey service, optional physical key-state
        /// reader, optional audio-settings persistence, the startup
        /// vocoder-readiness result, an optional audio stream factory,
        /// the optional codeplug systems seeding the FNE slice, the
        /// optional voice codec/traffic seams for the audio router,
        /// the optional FNE transport factory backing the connection
        /// slice, the optional codeplug backing the transmit-target
        /// resolver, the optional call-history store, the optional
        /// alias resolver backing the store's RID-alias column
        /// (per-system alias files, WPF-parity; a null resolver keeps
        /// the entries' aliases empty), the optional TAR settings
        /// persistence adapter backing the TAR configuration slice
        /// (WPF-compatible normalization over the Core merge-preserving
        /// settings store; a null adapter keeps the TAR slice absent
        /// and the view-model's save feedback permanently empty), and
        /// the optional PTT settings persistence adapter backing the
        /// PTT slice (shared settings store, WPF-parity; a null
        /// adapter keeps the PTT slice absent). The
        /// factory, readiness, systems, codec, sender, transport,
        /// codeplug, call-history store, alias-resolver, tar-persistence
        /// and ptt-persistence parameters are
        /// last so the pre-existing four-argument constructor remains
        /// source-compatible, including null-literal calls. When a
        /// key-state reader is present, exactly one 250 ms dispatcher
        /// timer polls the PTT hotkey and detaches on close. When audio
        /// settings are present, this window subscribes once to their
        /// property changes and applies selections to the ComboBoxes.
        /// When an audio stream factory is supplied, a talkgroup audio
        /// router is composed over it (owning the shared factory) with
        /// the injected codec and traffic seams; the null codec pair and
        /// stub sender keep headless construction inert. The receive glue
        /// is always composed and subscribes to every adapter the
        /// transport factory creates, so call history still records
        /// received frames when audio is unavailable. A null or empty
        /// systems list leaves the FNE slice dormant with zero rows; a
        /// populated list seeds the connection manager and service with
        /// the codeplug's real systems.
        /// </summary>
        public MainWindow(
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            IKeyboardKeyStateReader? keyStateReader,
            AudioSettingsPersistence? persistence,
            VocoderReadinessResult? vocoderStatus,
            IAudioStreamFactory? audioStreams = null,
            IReadOnlyList<Codeplug.System>? systems = null,
            IVoiceFrameDecoder? voiceDecoder = null,
            IVoiceFrameEncoder? voiceEncoder = null,
            IVoiceTrafficSender? voiceSender = null,
            IFneTransportFactory? transportFactory = null,
            Codeplug? codeplug = null,
            CallHistoryStore? callHistory = null,
            AliasResolver? aliasResolver = null,
            TarSettingsPersistence? tarPersistence = null,
            PttSettingsPersistence? pttPersistence = null,
            TarRecorder? tarRecorder = null,
            IAudioWaveFilePlayer? tarWaveFilePlayer = null,
            TarViewerColumnSettingsPersistence? tarViewerColumnPersistence = null)
            : this(
                catalog,
                hotkeys,
                keyStateReader,
                persistence,
                vocoderStatus,
                audioStreams,
                systems,
                voiceDecoder,
                voiceEncoder,
                voiceSender,
                transportFactory,
                codeplug,
                callHistory,
                aliasResolver,
                tarPersistence,
                pttPersistence,
                tarRecorder,
                tarWaveFilePlayer,
                tarViewerColumnPersistence,
                deferRuntimeActivation: false,
                ownsRuntimeServices: false)
        {
        }

        internal MainWindow(
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            IKeyboardKeyStateReader? keyStateReader,
            AudioSettingsPersistence? persistence,
            VocoderReadinessResult? vocoderStatus,
            IAudioStreamFactory? audioStreams,
            IReadOnlyList<Codeplug.System>? systems,
            IVoiceFrameDecoder? voiceDecoder,
            IVoiceFrameEncoder? voiceEncoder,
            IVoiceTrafficSender? voiceSender,
            IFneTransportFactory? transportFactory,
            Codeplug? codeplug,
            CallHistoryStore? callHistory,
            AliasResolver? aliasResolver,
            TarSettingsPersistence? tarPersistence,
            PttSettingsPersistence? pttPersistence,
            TarRecorder? tarRecorder,
            IAudioWaveFilePlayer? tarWaveFilePlayer,
            TarViewerColumnSettingsPersistence? tarViewerColumnPersistence,
            bool deferRuntimeActivation,
            bool ownsRuntimeServices)
        {
            InitializeComponent();
            this.hotkeys = hotkeys;
            this.ownsRuntimeServices = ownsRuntimeServices;
            audioSettingsPersistence = persistence;
            audioStreamFactory = audioStreams;
            macAudioDeviceCatalog = catalog as MacAudioDeviceCatalog;
            this.codeplug = codeplug;
            this.tarRecorder = tarRecorder;
            this.tarRecordingCoordinator = tarRecorder is null ? null : new TarRecordingCoordinator(tarRecorder);
            this.tarWaveFilePlayer = tarWaveFilePlayer;
            this.tarViewerColumnPersistence = tarViewerColumnPersistence;
            DataContext = new MainWindowViewModel(systems, catalog, hotkeys, persistence, vocoderStatus, codeplug, callHistory, tarPersistence, pttPersistence);

            // Compose the transmit-target resolver over the codeplug so
            // the PTT path resolves the primary channel's codeplug name
            // onto the router's transmit target; null keeps the PTT
            // path a no-op (the resolver never throws).
            transmitTargetResolver = codeplug is null ? null : new TransmitTargetResolver(codeplug);

            // Compose the headless FNE slice over the codeplug systems:
            // a null or empty list (missing/failed load) keeps it
            // dormant — no transport factory call is ever made and no
            // row can ever raise a request. The bridge is inert with
            // zero rows and safe to construct in headless tests. The
            // injected transport factory (the FnecoreTransportFactory
            // shared with App's voice traffic sender) is used when
            // supplied; otherwise a fresh fnecore-backed factory is
            // composed.
            if (DataContext is MainWindowViewModel viewModel)
            {
                var factory = transportFactory ?? new FnecoreTransportFactory();
                fnecoreTransportFactory = factory as FnecoreTransportFactory;
                fneConnectionService = new FneConnectionService(systems, factory);
                fneConnectionBridge = new FneConnectionServiceBridge(fneConnectionService, viewModel.FneConnections);
                fneConnectionBridge.Attach();
                AttachReceiveProjection(viewModel);

                if (fnecoreTransportFactory is { } commandFactory)
                {
                    subscriberCommandService = new SubscriberCommandService(
                        IsFneSystemAvailable,
                        systemName => commandFactory.ResolveAdapter(systemName),
                        TimeSpan.FromSeconds(10));
                }

                if (hotkeys is not null && viewModel.Ptt is { } ptt)
                {
                    hotkeyRegistrationCoordinator = new HotkeyRegistrationCoordinator(
                        hotkeys,
                        ptt,
                        OnHotkeyRegistrationStatusChanged);
                }
            }

            // Compose the receive glue independently of audio. Call
            // history and connection status must keep working when the
            // host has no audio catalog (non-macOS tests, a CoreAudio
            // permission failure, or a headless session). The optional
            // router is resolved at event time, so frames are still
            // classified and recorded while audio stays dormant.
            fneReceiveGlue = new FneReceiveGlue(
                (key, frame, mode) => talkgroupAudioRouter?.RouteVoiceFrame(key, frame, mode));

            if (codeplug is not null)
            {
                patchForwardingCoordinator = new PatchForwardingCoordinator(
                    codeplug,
                    voiceEncoder ?? new NullVoiceFrameEncoder(),
                    voiceSender ?? new StubVoiceTrafficSender(),
                    groupsMembershipContextKey,
                    downstreamObserver: tarRecordingCoordinator,
                    isSystemConnected: IsFneSystemAvailable);
            }

            if (fnecoreTransportFactory is { } receiveFactory)
            {
                receiveFactory.OnCreated += adapter =>
                {
                    adapter.DmrFrameReceived += e => fneReceiveGlue?.OnDmrFrame(adapter.ConfiguredSystemName, e);
                    adapter.P25FrameReceived += e => fneReceiveGlue?.OnP25Frame(adapter.ConfiguredSystemName, e);
                };
            }

            var receiveChannelResolver = codeplug is null
                ? null
                : new ReceiveChannelResolver(codeplug);

            if (fneReceiveGlue is { } receiveGlue)
            {
                // Wire the optional alias resolver into the store so
                // recorded entries carry the subscriber alias for their
                // (system, source id); unresolved aliases stay empty.
                if (callHistory is not null && aliasResolver is not null)
                {
                    callHistory.SetAliasResolver(
                        (system, src) => aliasResolver.Resolve(system, src) ?? string.Empty);
                }

                receiveGlue.CallFrameObserved += metadata =>
                {
                    var alias = aliasResolver?.Resolve(metadata.SystemName, metadata.SrcId);
                    receiveProjection?.Observe(metadata, alias, DateTimeOffset.UtcNow);
                    patchForwardingCoordinator?.HandleReceiveFrame(metadata);
                    var channelName = receiveChannelResolver?.Resolve(
                        metadata.SystemName, metadata.DstId, metadata.Slot);
                    tarRecordingCoordinator?.HandleReceiveFrame(
                        metadata,
                        channelName,
                        alias,
                        isEncrypted: false,
                        encryptionAlgorithm: null,
                        encryptionKeyId: null,
                        DateTime.UtcNow);
                    callHistory?.AddFrame(metadata, channelName);
                };

                if (callHistory is not null)
                {
                    callHistory.Changed += () => Dispatcher.UIThread.Post(() =>
                    {
                        if (DataContext is MainWindowViewModel vm)
                        {
                            vm.CallHistory?.Refresh();
                        }
                    });
                }
            }

            // Compose the talkgroup audio router over the shared factory
            // when one was supplied; the router owns the factory and is
            // disposed on window close. The null codec pair and the stub
            // traffic sender keep the audio slice inert until native
            // vocoder readiness succeeds.
            if (audioStreams is not null && DataContext is MainWindowViewModel audioViewModel)
            {
                var decoder = voiceDecoder ?? new NullVoiceFrameDecoder();
                var encoder = voiceEncoder ?? new NullVoiceFrameEncoder();
                var sender = voiceSender ?? new StubVoiceTrafficSender();

                talkgroupAudioRouter = new TalkgroupAudioRouter(
                    audioStreams,
                    decoder,
                    encoder,
                    sender,
                    () => audioViewModel.AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default,
                    decodedPcmObserver: (IDecodedPcmObserver?)patchForwardingCoordinator ?? tarRecordingCoordinator,
                    transmittedPcmObserver: tarRecordingCoordinator,
                    resolveMonitorEnabled: audioViewModel.IsMonitorEnabled,
                    resolveTalkgroupOutputDevice: audioViewModel.ResolveMonitorOutputDevice,
                    resolveTalkgroupVolume: audioViewModel.ResolveMonitorVolume,
                    resolveSpeakerOutputEnabled: _ => !ShouldMuteRxPlayback());

                if (audioViewModel.Ptt is { } ptt)
                {
                    ptt.PttStateRequested += OnPttStateRequested;
                }

                audioViewModel.ChannelSelectionChanged += OnChannelSelectionChanged;
                audioViewModel.ChannelVolumeChanged += OnChannelVolumeChanged;
                audioViewModel.ChannelOutputDeviceChanged += OnChannelOutputDeviceChanged;

                talkgroupAudioRouter.CaptureEnded += OnCaptureEnded;
                talkgroupAudioRouter.MonitorStreamEnded += OnMonitorStreamEnded;
                talkgroupAudioRouter.TalkgroupStreamEnded += OnTalkgroupStreamEnded;

                toneDispatchCoordinator = new ToneDispatchRuntimeCoordinator(
                    ResolveToneDispatchTargets,
                    targets => targets.All(IsTransmitTargetAvailable),
                    () => audioViewModel.Ptt?.IsEngaged == true
                        || Volatile.Read(ref dashboardTransmitActive) != 0
                        || patchPttRuntimeCoordinator?.IsTransmitActive == true,
                    (targets, pcm, sendStartSignal, cancellationToken) =>
                        talkgroupAudioRouter.TransmitPcmAsync(
                            targets,
                            pcm,
                            sendStartSignal,
                            cancellationToken),
                    status => Dispatcher.UIThread.Post(() =>
                    {
                        if (DataContext is MainWindowViewModel statusViewModel)
                        {
                            statusViewModel.AudioStatusMessage = status;
                        }
                    }),
                    (pcm, cancellationToken) => talkgroupAudioRouter.PlayLocalPcmAsync(pcm));

                if (transmitTargetResolver is { } resolver)
                {
                    patchPttRuntimeCoordinator = new PatchPttRuntimeCoordinator(
                        resolver,
                        targets => BeginPatchTransmitAsync(
                            talkgroupAudioRouter,
                            targets,
                            audioViewModel),
                        () => EndTransmitAndStopRecordingAsync(talkgroupAudioRouter),
                        routerTargets => talkgroupAudioRouter.ClearAllTalkgroupBuffers(),
                        () => audioViewModel.Ptt?.IsEngaged != true
                            && Volatile.Read(ref dashboardTransmitActive) == 0,
                        target => IsTransmitTargetAvailable(target),
                        status => Dispatcher.UIThread.Post(() =>
                        {
                            if (DataContext is MainWindowViewModel statusViewModel)
                            {
                                statusViewModel.AudioStatusMessage = status;
                            }
                        }),
                        target => patchForwardingCoordinator?.IsForwardTargetActive(
                            target.SystemName,
                            target.TalkgroupId) == true);
                }

                if (transmitTargetResolver is { } channelResolver)
                {
                    channelPttRuntimeCoordinator = new ChannelPttRuntimeCoordinator(
                        channelResolver,
                        targets => BeginChannelTransmitAsync(
                            talkgroupAudioRouter,
                            targets,
                            audioViewModel),
                        () => EndChannelTransmitAsync(talkgroupAudioRouter),
                        targets => talkgroupAudioRouter.ClearAllTalkgroupBuffers(),
                        () => audioViewModel.Ptt?.IsEngaged != true
                            && Volatile.Read(ref dashboardTransmitActive) == 0
                            && patchPttRuntimeCoordinator?.IsTransmitActive != true,
                        target => IsTransmitTargetAvailable(target),
                        status => Dispatcher.UIThread.Post(() =>
                        {
                            if (DataContext is MainWindowViewModel statusViewModel)
                            {
                                statusViewModel.AudioStatusMessage = status;
                            }
                        }));
                }

            }

            if (DataContext is MainWindowViewModel viewModelWithSettings
                && viewModelWithSettings.AudioSettings is { } settings)
            {
                settings.PropertyChanged += AudioSettings_PropertyChanged;
            }

            Closed += OnWindowClosed;
            this.keyStateReader = keyStateReader;

            if (!deferRuntimeActivation)
            {
                ActivateRuntime();
            }
        }

        /// <summary>
        /// Starts the timers, event subscriptions, and restored stream work
        /// that make a composed window live. Reload candidates are composed
        /// deferred and activate only after the previous runtime has stopped.
        /// </summary>
        internal void ActivateRuntime()
        {
            if (runtimeActivated)
            {
                return;
            }

            runtimeActivated = true;
            diagnosticLogSink?.WriteApplication(
                LogLevel.INFO,
                "application runtime activated");

            if (receiveProjection is not null && receiveProjectionTimer is null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += OnReceiveProjectionTimerTick;
                timer.Start();
                receiveProjectionTimer = timer;
            }

            if (macAudioDeviceCatalog is { } macCatalog)
            {
                macCatalog.DevicesChanged += OnAudioDevicesChanged;
            }

            if (tarRecordingCoordinator is { } recordingCoordinator
                && tarRetentionTimer is null)
            {
                recordingCoordinator.RunRetentionMaintenance();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
                timer.Tick += (_, _) => recordingCoordinator.RunRetentionMaintenance();
                timer.Start();
                tarRetentionTimer = timer;
            }

            if (hotkeys is not null)
            {
                hotkeys.HotkeyPressed += OnHotkeyPressed;
            }

            if (DataContext is MainWindowViewModel viewModel
                && viewModel.AudioSettings is { } settings)
            {
                ApplyAudioSelections();
            }

            if (keyStateReader is not null && watchdogTimer is null)
            {
                watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                watchdogTimer.Tick += OnWatchdogTick;
                watchdogTimer.Start();
            }

            if (webStreamShell is not null)
            {
                _ = ObserveAsync(
                    webStreamShell.StartRestoredAsync(),
                    "Web-stream restore startup failed");
            }
        }

        public void AttachPreferencesPersistence(PreferencesSettingsPersistence preferencesPersistence)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AttachPreferencesPersistence(preferencesPersistence);
                if (viewModel.Preferences is { } preferences)
                {
                    preferences.PropertyChanged += OnShellPreferencesChanged;
                    ApplyTheme(preferences.DarkMode);
                    Topmost = preferences.KeepWindowOnTop || Topmost;
                }
            }
        }

        private void OnShellPreferencesChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not OperatorPreferencesViewModel preferences)
                return;

            if (e.PropertyName == nameof(OperatorPreferencesViewModel.DarkMode))
                ApplyTheme(preferences.DarkMode);

            if (e.PropertyName == nameof(OperatorPreferencesViewModel.KeepWindowOnTop))
            {
                Topmost = preferences.KeepWindowOnTop;
                if (layoutSection is not null)
                {
                    layoutSection.KeepWindowOnTop = Topmost;
                    SaveLayoutSection();
                }
            }
        }

        /// <summary>
        /// Attaches the shared groups settings adapter after construction,
        /// preserving the existing MainWindow constructor ABI.
        /// </summary>
        public void AttachGroupsPersistence(GroupSettingsPersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(persistence);
            if (groupsPersistence is not null)
            {
                return;
            }

            groupsPersistence = persistence;
            if (DataContext is MainWindowViewModel viewModel)
            {
                UserSettingsGroupSection section = new();
                try
                {
                    if (persistence.TryLoad(out UserSettingsGroupSection loaded))
                    {
                        section = loaded;
                    }
                }
                catch (Exception exception)
                {
                    WriteApplicationException("Groups settings load failed", exception);
                }

                viewModel.ApplyGroupsSection(
                    section,
                    groupsMembershipContextKey,
                    viewModel.Preferences?.RetainPatchStateOnStartup == true);
            }
        }

        public void AttachRestorePersistence(RestoreSettingsPersistence restorePersistence)
        {
            this.restoreSettingsPersistence = restorePersistence;
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AttachRestorePersistence(restorePersistence);
            }
        }

        /// <summary>Attaches the shared web-stream source factory.</summary>
        public void AttachWebStreamSourceFactory(IWebStreamSourceFactory sourceFactory)
        {
            ArgumentNullException.ThrowIfNull(sourceFactory);
            webStreamSourceFactory ??= sourceFactory;
        }

        /// <summary>
        /// Configures the shell-owned codeplug reload boundary. Candidate
        /// construction happens after parsing but before this window's runtime
        /// is stopped; installation is invoked only after awaited teardown.
        /// </summary>
        internal void ConfigureCodeplugReload(
            Func<Codeplug, string, MainWindow> createWindow,
            Func<MainWindow, MainWindow, Task> installWindow,
            string initialCodeplugPath)
        {
            createCodeplugWindow = createWindow
                ?? throw new ArgumentNullException(nameof(createWindow));
            installCodeplugWindow = installWindow
                ?? throw new ArgumentNullException(nameof(installWindow));
            codeplugPath = initialCodeplugPath ?? string.Empty;

            MainWindow? candidate = null;
            codeplugReloadCoordinator = new CodeplugReloadCoordinator(
                CodeplugLoader.LoadFromFile,
                DisposeRuntimeAsync,
                async loadedCodeplug =>
                {
                    if (candidate is null)
                    {
                        throw new InvalidOperationException(
                            "Codeplug reload candidate was not prepared.");
                    }

                    await installCodeplugWindow(this, candidate).ConfigureAwait(false);
                    candidate = null;
                },
                status => SetCodeplugStatus(status),
                loadedCodeplug =>
                {
                    candidate = createCodeplugWindow(
                        loadedCodeplug,
                        pendingCodeplugPath ?? codeplugPath);
                    return Task.CompletedTask;
                },
                async () =>
                {
                    if (candidate is not null)
                    {
                        await candidate.DisposeRuntimeAsync().ConfigureAwait(false);
                        candidate = null;
                    }
                });
        }

        /// <summary>Opens the platform file picker and reloads one codeplug.</summary>
        internal async Task OpenCodeplugAsync()
        {
            if (codeplugReloadCoordinator is null)
            {
                return;
            }

            string? initialDirectory = string.IsNullOrWhiteSpace(codeplugPath)
                ? null
                : Path.GetDirectoryName(codeplugPath);
            FileDialogResult result = await FileDialogService.OpenFileAsync(
                new OpenFileRequest(
                    "Open Codeplug",
                    new[]
                    {
                        new DvmConsole.Platform.Dialogs.FileDialogFilter(
                            "Codeplug files",
                            new[] { "*.yml", "*.yaml" }),
                    },
                    false,
                    initialDirectory),
                CancellationToken.None).ConfigureAwait(true);

            if (result.Cancelled || string.IsNullOrWhiteSpace(result.Selected))
            {
                return;
            }

            string selectedPath = result.Selected;
            pendingCodeplugPath = selectedPath;
            try
            {
                if (await codeplugReloadCoordinator.ReloadAsync(
                        selectedPath,
                        CancellationToken.None).ConfigureAwait(true))
                {
                    codeplugPath = selectedPath;
                }
            }
            finally
            {
                pendingCodeplugPath = null;
            }
        }

        private void SetCodeplugStatus(string status)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.CodeplugStatusMessage = status;
            }
        }

        public void AttachLayoutPersistence(LayoutSettingsPersistence layoutPersistence)
        {
            if (layoutPersistence is null || layoutHydrated)
            {
                return;
            }

            this.layoutPersistence = layoutPersistence;
            layoutPersistence.TryLoad(out UserSettingsLayoutSection section);
            layoutSection = section;

            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SetWidgetVisibility(
                    section.ShowSystemStatus,
                    section.ShowChannels,
                    section.ShowAlertTones);
            }

            Width = section.WindowWidth;
            Height = section.WindowHeight;
            bool? preferenceKeepWindowOnTop =
                (DataContext as MainWindowViewModel)?.Preferences?.KeepWindowOnTop;
            Topmost = ResolveKeepWindowOnTop(
                preferenceKeepWindowOnTop,
                section.KeepWindowOnTop);
            section.KeepWindowOnTop = Topmost;
            ApplyBackground(section.UserBackgroundImage);
            if (section.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            layoutHydrated = true;
        }

        /// <summary>
        /// Composes the configured web-stream shell after restore and layout
        /// sections have been hydrated. Source and audio factories are borrowed
        /// shared owners; each item owns only its coordinator and per-run output.
        /// </summary>
        public void AttachWebStreamPersistence(
            RestoreSettingsPersistence restorePersistence,
            LayoutSettingsPersistence layoutPersistence)
        {
            if (webStreamShell is not null
                || DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            UserSettingsRestoreSection restoreSection = new();
            UserSettingsAudioSection audioSection = new();
            try
            {
                restorePersistence.TryLoad(out restoreSection);
                audioSettingsPersistence?.TryLoad(out audioSection);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Web-stream settings load failed", exception);
            }

            var definitions = (codeplug?.Zones ?? new List<Codeplug.Zone>())
                .SelectMany(zone => (zone?.WebStreams ?? new List<Codeplug.WebStream>())
                    .Select(stream => new WebStreamShellDefinition(stream, zone?.Name ?? string.Empty)))
                .ToList();
            var positions = this.layoutSection?.WebStreamPositions
                ?? new Dictionary<string, UserSettingsLayoutPosition>(StringComparer.OrdinalIgnoreCase);
            webStreamShell = new WebStreamShellViewModel(
                definitions,
                viewModel.Preferences?.RestoreSelectedChannelsOnStartup == true,
                restoreSection.SelectedWebStreams,
                audioSection.WebStreamVolumes,
                positions,
                webStreamSourceFactory,
                audioStreamFactory,
                () => viewModel.AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default,
                action => Dispatcher.UIThread.Post(action));
            viewModel.AttachWebStreams(webStreamShell);
            if (runtimeActivated)
            {
                _ = ObserveAsync(
                    webStreamShell.StartRestoredAsync(),
                    "Web-stream restore startup failed");
            }
        }

        public void AttachAlertSettingsPersistence(AlertSettingsPersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(persistence);
            alertSettingsPersistence ??= persistence;
        }

        /// <summary>
        /// Attaches the app-lifetime diagnostic sink after construction so
        /// legacy constructor shapes remain unchanged. The sink is shared by
        /// the FNE factory, audio router, and debug-log viewer.
        /// </summary>
        internal void AttachDiagnosticLogSink(DiagnosticLogSink sink)
        {
            diagnosticLogSink ??= sink ?? throw new ArgumentNullException(nameof(sink));
            if (talkgroupAudioRouter is not null)
            {
                talkgroupAudioRouter.DiagnosticWriter = diagnosticLogSink.Write;
            }
        }

        public void AttachSettingsTransfer(SettingsTransferService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            settingsTransferService ??= service;
        }

        public void AttachAlertTonePreview(
            IAudioWaveFileInspector inspector,
            IAudioWaveFilePlayer? player)
        {
            ArgumentNullException.ThrowIfNull(inspector);
            alertTonePreviewInspector ??= inspector;
            alertTonePreviewPlayer ??= player;
        }

        private void AttachReceiveProjection(MainWindowViewModel viewModel)
        {
            receiveProjectionViewModel = viewModel;
            receiveProjection = new ReceiveProjection(
                action => Dispatcher.UIThread.Post(action),
                () => receiveProjectionViewModel?.Channels
                    ?? Array.Empty<ChannelSlotViewModel>());

            viewModel.PropertyChanged += MainWindowViewModel_PropertyChanged;
            foreach (var system in viewModel.FneConnections.Systems)
            {
                system.PropertyChanged += FneSystemConnection_PropertyChanged;
                ApplyFneConnectionWarning(system);
            }

            receiveProjection.Reproject();
        }

        private void MainWindowViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainWindowViewModel.Channels)
                or nameof(MainWindowViewModel.SelectedZone))
            {
                receiveProjection?.Reproject();
            }
        }

        private void FneSystemConnection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is FneSystemConnectionViewModel system
                && e.PropertyName is nameof(FneSystemConnectionViewModel.IsConnected)
                    or nameof(FneSystemConnectionViewModel.IsStarted))
            {
                ApplyFneConnectionWarning(system);
            }
        }

        private void ApplyFneConnectionWarning(FneSystemConnectionViewModel system)
        {
            var connected = system.IsConnected && system.IsStarted;
            var detail = connected
                ? null
                : system.IsConnected
                    ? "FNE system not started"
                    : "FNE disconnected";
            receiveProjection?.SetFneConnectionWarning(system.SystemName, connected, detail);
        }

        private bool IsFneSystemAvailable(string systemName)
            => fneConnectionService?.GetSnapshot(systemName) is
            {
                IsConnected: true,
                IsStarted: true,
            };

        private bool IsTransmitTargetAvailable(TransmitTarget target)
        {
            if (!IsFneSystemAvailable(target.SystemName))
            {
                return false;
            }

            if (fnecoreTransportFactory?.ResolveAdapter(target.SystemName)
                is not IFneTalkgroupStatusProvider provider)
            {
                // Compatibility/fake transports do not expose announced
                // rules yet; connection validation remains their only gate.
                return true;
            }

            if (!uint.TryParse(target.TalkgroupId, out uint talkgroupId))
            {
                return false;
            }

            var query = new TalkgroupQuery(
                talkgroupId,
                target.Slot,
                target.Mode == VoiceMode.P25 ? TalkgroupMode.P25 : TalkgroupMode.Dmr);
            return provider.QueryTalkgroupAvailability(query).IsAvailable;
        }

        private void OnReceiveProjectionTimerTick(object? sender, EventArgs e)
            => receiveProjection?.SweepIdle(DateTimeOffset.UtcNow);

        /// <summary>
        /// Re-applies both ComboBox selections whenever the audio-settings
        /// slice reports a device-list or selection-id change. List
        /// notifications are mandatory because <see cref="AudioSettingsViewModel.Refresh"/>
        /// replaces row instances wholesale; the mapper re-resolves the
        /// saved id against the current rows.
        /// </summary>
        private void AudioSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AudioSettingsViewModel.InputDevices)
                or nameof(AudioSettingsViewModel.OutputDevices)
                or nameof(AudioSettingsViewModel.SelectedInputId)
                or nameof(AudioSettingsViewModel.SelectedOutputId))
            {
                ApplyAudioSelections();
                if (e.PropertyName is nameof(AudioSettingsViewModel.OutputDevices)
                    or nameof(AudioSettingsViewModel.SelectedOutputId))
                {
                    RestartSelectedMonitors();
                }
            }
        }

        private void RestartSelectedMonitors()
        {
            if (talkgroupAudioRouter is not { } router
                || DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            foreach (var slot in viewModel.SelectedChannels)
            {
                if (!string.IsNullOrWhiteSpace(slot.ResourceKey))
                {
                    router.StopMonitor(slot.ResourceKey);
                }
            }
        }

        private void OnChannelSelectionChanged(ChannelSlotViewModel slot, bool isSelected)
        {
            if (isSelected || string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            Dispatcher.UIThread.Post(() => talkgroupAudioRouter?.StopMonitor(slot.ResourceKey));
        }

        private void OnChannelVolumeChanged(ChannelSlotViewModel slot)
        {
            if (string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
                talkgroupAudioRouter?.SetMonitorVolume(slot.ResourceKey, (float)slot.Volume));
        }

        private void OnChannelOutputDeviceChanged(ChannelSlotViewModel slot)
        {
            if (string.IsNullOrWhiteSpace(slot.ResourceKey))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
                talkgroupAudioRouter?.StopMonitor(slot.ResourceKey));
        }

        /// <summary>
        /// Applies the audio-settings slice's saved selections to the
        /// input and output ComboBoxes by resolving each id to its option
        /// row with <see cref="AudioDeviceSelectionMapper.FindById"/>.
        /// Null-safe: a null view-model, slice, or unmapped id is a no-op
        /// (the selection clears). Selection ids are never converted or
        /// otherwise written here — the view-model remains the single
        /// source of truth for the saved ids.
        /// </summary>
        private void ApplyAudioSelections()
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.AudioSettings is not { } settings)
            {
                return;
            }

            AudioInputComboBox.SelectedItem =
                AudioDeviceSelectionMapper.FindById(settings.InputDevices, settings.SelectedInputId);
            AudioOutputComboBox.SelectedItem =
                AudioDeviceSelectionMapper.FindById(settings.OutputDevices, settings.SelectedOutputId);
        }

        /// <summary>
        /// Forwards a user-picked input row to the audio-settings slice.
        /// Safe no-op for any other sender, null selection, null data
        /// context, or absent slice.
        /// </summary>
        private void AudioInputComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox comboBox
                || comboBox.SelectedItem is not AudioDeviceOptionViewModel row
                || DataContext is not MainWindowViewModel viewModel
                || viewModel.AudioSettings is not { } settings)
            {
                return;
            }

            settings.SelectedInputId = row.Id;
        }

        /// <summary>
        /// Forwards a user-picked output row to the audio-settings slice.
        /// Safe no-op for any other sender, null selection, null data
        /// context, or absent slice.
        /// </summary>
        private void AudioOutputComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox comboBox
                || comboBox.SelectedItem is not AudioDeviceOptionViewModel row
                || DataContext is not MainWindowViewModel viewModel
                || viewModel.AudioSettings is not { } settings)
            {
                return;
            }

            settings.SelectedOutputId = row.Id;
        }

        /// <summary>
        /// Request-only Save wiring: forwards a Save request to the
        /// audio-settings slice via <see cref="AudioSettingsViewModel.Commit"/>,
        /// which raises <see cref="AudioSettingsViewModel.SaveRequested"/>
        /// with the current selection and AGC state. Nothing is persisted,
        /// subscribed, or touched natively here. Safe no-op when the
        /// view-model or slice is absent.
        /// </summary>
        private void AudioSave_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AudioSettings?.Commit();
            }
        }

        /// <summary>
        /// File and folder picker service used by shell features. Injected by
        /// <see cref="App"/> with the window's storage provider; the no-op
        /// fallback keeps the shell behaviorally unchanged until then.
        /// </summary>
        internal IFileDialogService FileDialogService { get; set; } = NoopFileDialogService.Instance;

        /// <summary>
        /// Host-owned file-manager reveal adapter for the TAR Viewer.
        /// Headless construction keeps a safe no-op fallback; App replaces
        /// it with the desktop adapter during production composition.
        /// </summary>
        internal IFileRevealService TarFileRevealService { get; set; } = NoopFileRevealService.Instance;

        /// <summary>
        /// Host-owned confirmation adapter for destructive TAR Viewer actions.
        /// Headless construction keeps a deny-by-default fallback; App replaces
        /// it with the Avalonia modal adapter during production composition.
        /// </summary>
        internal IConfirmationService TarConfirmationService { get; set; } = NoopConfirmationService.Instance;

        /// <summary>
        /// Opens the TAR configuration dialog with this window as its
        /// owner. The dialog is constructed over the composed TAR
        /// configuration view-model and this window's
        /// <see cref="FileDialogService"/>; persistence stays owned by
        /// the view-model (its <c>SaveRequested</c> subscription is
        /// composed upstream), so nothing is saved or written here. Safe
        /// no-op when the data context is not a
        /// <see cref="MainWindowViewModel"/> or its TAR configuration
        /// slice is absent (no codeplug or no TAR persistence).
        /// </summary>
        internal void OpenTarConfiguration()
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.TarConfiguration is not { } tarConfiguration)
            {
                return;
            }

            var dialog = new TarConfigurationWindow(tarConfiguration, FileDialogService);
            _ = dialog.ShowDialog(this);
        }

        /// <summary>
        /// Opens the settings-transfer dialog over the shared transfer service.
        /// A successful import or reset re-runs the current codeplug candidate
        /// through the existing stop/install lifecycle exactly once.
        /// </summary>
        internal void OpenSettingsTransfer()
        {
            if (settingsTransferWindow is not null
                || settingsTransferService is not { } service)
            {
                return;
            }

            if (DataContext is not MainWindowViewModel)
            {
                return;
            }

            Func<Task> reloadRuntimeAsync = ReloadCurrentRuntimeAsync;
            var viewModel = new SettingsTransferViewModel(service);
            var window = new SettingsTransferWindow(
                viewModel,
                FileDialogService,
                TarConfirmationService,
                reloadRuntimeAsync);
            settingsTransferWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(settingsTransferWindow, window))
                {
                    settingsTransferWindow = null;
                }
            };
            _ = window.ShowDialog(this);
        }

        /// <summary>
        /// Opens one modeless debug-log viewer over the app-lifetime buffer.
        /// Re-entry activates the existing window instead of creating a
        /// second subscription to the shared buffer.
        /// </summary>
        internal void OpenDebugLog()
        {
            if (debugLogWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            if (diagnosticLogSink is not { } sink)
                return;

            var window = new DebugLogWindow(
                new DebugLogViewModel(sink.Buffer),
                FileDialogService);
            debugLogWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(debugLogWindow, window))
                    debugLogWindow = null;
            };
            window.Show(this);
        }

        internal void ToggleSelectAllCurrentZone()
        {
            if (DataContext is MainWindowViewModel viewModel)
                viewModel.ToggleSelectAllCurrentZone();
        }

        internal void OpenCallHistory()
        {
            CallHistoryFilterTextBox.Focus();
        }

        internal void OpenWidgetSelection()
        {
            if (widgetSelectionWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            if (DataContext is not MainWindowViewModel viewModel)
                return;

            var dialog = new WidgetSelectionWindow(
                viewModel.ShowSystemStatus,
                viewModel.ShowChannels,
                viewModel.ShowAlertTones);
            widgetSelectionWindow = dialog;
            dialog.SaveRequested += (showSystemStatus, showChannels, showAlertTones) =>
            {
                viewModel.SetWidgetVisibility(showSystemStatus, showChannels, showAlertTones);
                if (layoutSection is not null)
                {
                    layoutSection.ShowSystemStatus = showSystemStatus;
                    layoutSection.ShowChannels = showChannels;
                    layoutSection.ShowAlertTones = showAlertTones;
                    SaveLayoutSection();
                }
            };
            dialog.Closed += (_, _) =>
            {
                if (ReferenceEquals(widgetSelectionWindow, dialog))
                    widgetSelectionWindow = null;
            };
            dialog.Show(this);
        }

        internal async void OpenUserBackgroundAsync()
        {
            try
            {
                FileDialogResult result = await FileDialogService.OpenFileAsync(
                    new OpenFileRequest(
                        "Select User Background",
                        new[]
                        {
                            new DvmConsole.Platform.Dialogs.FileDialogFilter(
                                "Image files", new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }),
                        },
                        false,
                        null),
                    CancellationToken.None);
                if (result.Cancelled || string.IsNullOrWhiteSpace(result.Selected))
                    return;

                bool applied = ApplyBackground(result.Selected);
                if (layoutSection is not null)
                {
                    layoutSection.UserBackgroundImage = applied ? result.Selected : null;
                    SaveLayoutSection();
                }
            }
            catch (OperationCanceledException)
            {
                // Picker cancellation is a normal outcome.
            }
            catch (Exception exception)
            {
                WriteApplicationException("User background selection failed", exception);
            }
        }

        internal async void ResetSettings()
        {
            bool confirmed = await TarConfirmationService.ConfirmAsync(
                this,
                new ConfirmationRequest(
                    "Reset Settings",
                    "Reset console settings? The defaults take effect after restart."),
                CancellationToken.None);
            if (confirmed)
                settingsTransferService?.Reset();
        }

        internal void ResetLayout()
        {
            if (layoutSection is null)
                return;

            layoutSection.ChannelPositions.Clear();
            layoutSection.SystemStatusPositions.Clear();
            layoutSection.AlertTonePositions.Clear();
            layoutSection.WebStreamPositions.Clear();
            layoutSection.CanvasWidth = Width;
            layoutSection.CanvasHeight = Height;
            SaveLayoutSection();
        }

        internal void FitLayoutToWindow()
        {
            if (layoutSection is null)
                return;

            layoutSection.CanvasWidth = Width;
            layoutSection.CanvasHeight = Height;
            SaveLayoutSection();
        }

        internal void SetWidgetLayoutLocked()
        {
            if (layoutSection is null)
                return;

            layoutSection.LockWidgets = !layoutSection.LockWidgets;
            SaveLayoutSection();
            if (layoutSection.LockWidgets)
            {
                draggedWebStream = null;
                FneConnectionsPanel.Focus();
            }
        }

        internal void ToggleKeepWindowOnTop()
        {
            Topmost = !Topmost;
            if (DataContext is MainWindowViewModel viewModel
                && viewModel.Preferences is { } preferences)
            {
                preferences.KeepWindowOnTop = Topmost;
            }
            if (layoutSection is not null)
            {
                layoutSection.KeepWindowOnTop = Topmost;
                SaveLayoutSection();
            }
        }

        internal void OpenFneConnectionManager()
        {
            if (DataContext is MainWindowViewModel viewModel
                && !viewModel.ShowSystemStatus)
            {
                viewModel.SetWidgetVisibility(
                    showSystemStatus: true,
                    showChannels: viewModel.ShowChannels,
                    showAlertTones: viewModel.ShowAlertTones);
                if (layoutSection is not null)
                {
                    layoutSection.ShowSystemStatus = true;
                    SaveLayoutSection();
                }
            }

            FneConnectionsPanel.Focus();
        }

        internal bool CanOpenSubscriberCommands
            => subscriberCommandService is not null
                && codeplug?.Systems is { Count: > 0 }
                && DataContext is MainWindowViewModel viewModel
                && viewModel.FneConnections.AnyConnected;

        internal void OpenSubscriberCommand(SubscriberCommandKind commandKind)
        {
            if (subscriberCommandService is not { } service
                || codeplug?.Systems is not { } systems
                || systems.Count == 0)
            {
                return;
            }

            if (subscriberCommandWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            string title = commandKind switch
            {
                SubscriberCommandKind.Page => "Page Subscriber",
                SubscriberCommandKind.RadioCheck => "Radio Check Subscriber",
                SubscriberCommandKind.Inhibit => "Inhibit Subscriber",
                SubscriberCommandKind.Uninhibit => "Uninhibit Subscriber",
                _ => "Subscriber Command",
            };
            var viewModel = new SubscriberCommandViewModel(
                systems,
                commandKind,
                "subscriber-command-window",
                service.ExecuteAsync);
            var dialog = new SubscriberCommandWindow(viewModel, title);
            subscriberCommandWindow = dialog;
            dialog.Closed += (_, _) =>
            {
                if (ReferenceEquals(subscriberCommandWindow, dialog))
                    subscriberCommandWindow = null;
            };
            _ = dialog.ShowDialog(this);
        }

        internal bool CanOpenQuickCall
            => toneDispatchCoordinator is not null;

        internal void OpenManualQuickCall()
        {
            if (quickCallWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            var dialog = new QuickCallWindow(new QuickCallViewModel());
            quickCallWindow = dialog;
            Action<QuickCallRequest> sendRequested = request =>
                _ = SendQuickCallAsync(request);
            dialog.SendRequested += sendRequested;
            dialog.Closed += (_, _) =>
            {
                dialog.SendRequested -= sendRequested;
                if (ReferenceEquals(quickCallWindow, dialog))
                    quickCallWindow = null;
            };
            _ = dialog.ShowDialog(this);
        }

        private async Task SendQuickCallAsync(QuickCallRequest request)
        {
            if (toneDispatchCoordinator is not { } coordinator)
                return;

            var pageSlots = ResolveQuickCallPageSlots();
            var targets = transmitTargetResolver is { } resolver
                ? resolver.ResolveAll(pageSlots.Select(slot => slot.ChannelName))
                : Array.Empty<TransmitTarget>();
            bool sent = await coordinator.SendGeneratedPcmAsync(
                    request.Pcm,
                    request.SendStartSignal,
                    CancellationToken.None,
                    targets)
                .ConfigureAwait(false);
            if (!sent || !request.ClearPageStateAfterSend)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (ChannelSlotViewModel slot in pageSlots)
                    slot.PageState = false;
            });
        }

        private IReadOnlyList<ChannelSlotViewModel> ResolveQuickCallPageSlots()
        {
            if (DataContext is not MainWindowViewModel viewModel)
                return Array.Empty<ChannelSlotViewModel>();

            return viewModel.SelectedChannels
                .Where(slot => slot.IsSelected && slot.PageState && !slot.IsRxOnly)
                .ToArray();
        }

        internal static bool ResolveKeepWindowOnTop(
            bool? preferenceValue,
            bool layoutValue)
            => preferenceValue ?? layoutValue;

        private void ApplyTheme(bool darkMode)
        {
            RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
            if (string.IsNullOrWhiteSpace(userBackgroundPath))
            {
                Background = new SolidColorBrush(
                    Color.Parse(darkMode ? "#0B1114" : "#F3F6F7"));
            }
        }

        private bool ApplyBackground(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    var bitmap = new Bitmap(path);
                    var previousBitmap = userBackgroundBitmap;
                    userBackgroundBitmap = bitmap;
                    userBackgroundPath = path;
                    Background = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill,
                    };
                    previousBitmap?.Dispose();
                    return true;
                }
                catch (Exception exception)
                {
                    WriteApplicationException("User background could not be loaded", exception);
                }
            }

            userBackgroundBitmap?.Dispose();
            userBackgroundBitmap = null;
            userBackgroundPath = null;
            if (layoutSection is not null)
                layoutSection.UserBackgroundImage = null;

            bool darkMode = (DataContext as MainWindowViewModel)?.Preferences?.DarkMode == true;
            Background = new SolidColorBrush(
                Color.Parse(darkMode ? "#0B1114" : "#F3F6F7"));
            return false;
        }

        private async Task ReloadCurrentRuntimeAsync()
        {
            if (codeplugReloadCoordinator is null
                || string.IsNullOrWhiteSpace(codeplugPath))
            {
                return;
            }

            pendingCodeplugPath = codeplugPath;
            bool previous = suppressRuntimeSettingsPersistence;
            suppressRuntimeSettingsPersistence = true;
            try
            {
                await codeplugReloadCoordinator.ReloadAsync(
                    codeplugPath,
                    CancellationToken.None);
            }
            finally
            {
                suppressRuntimeSettingsPersistence = previous;
                pendingCodeplugPath = null;
            }
        }

        /// <summary>
        /// Opens one modeless alert-tone manager over the shared alert settings
        /// section. The VM performs managed normalization only; this shell
        /// owns file availability, persistence, and dialog adapters.
        /// </summary>
        internal void OpenAlertToneManager()
        {
            if (alertToneManagerWindow is not null
                || alertSettingsPersistence is not { } persistence)
            {
                return;
            }

            if (!persistence.TryLoad(out UserSettingsAlertSection section))
            {
                section = new UserSettingsAlertSection();
            }

            var configs = section.AlertTones is { Count: > 0 }
                ? section.AlertTones
                : (section.AlertToneFilePaths ?? new List<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new UserSettingsAlertToneConfig
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DisplayName = GetAlertToneDisplayName(path),
                        FilePath = path,
                        TabName = section.AlertToneTabs.TryGetValue(path, out string? tab)
                            ? tab
                            : string.Empty,
                        Position = section.AlertTonePositions.TryGetValue(
                                path,
                                out UserSettingsLayoutPosition? position)
                            ? position
                            : new UserSettingsLayoutPosition { X = 20, Y = 20 },
                    })
                    .ToList();

            var manager = new AlertToneManagerViewModel(
                configs,
                Array.Empty<string>(),
                path =>
                {
                    try
                    {
                        return System.IO.File.Exists(path);
                    }
                    catch
                    {
                        return false;
                    }
                });
            Action<IReadOnlyList<UserSettingsAlertToneConfig>> saveRequested =
                snapshot => SaveAlertToneSection(persistence, snapshot);
            manager.SaveRequested += saveRequested;

            var window = new AlertToneManagerWindow(
                manager,
                FileDialogService,
                TarConfirmationService,
                alertTonePreviewInspector,
                alertTonePreviewPlayer);
            alertToneManagerWindow = window;
            window.Closed += (_, _) =>
            {
                manager.SaveRequested -= saveRequested;
                if (ReferenceEquals(alertToneManagerWindow, window))
                {
                    alertToneManagerWindow = null;
                }
            };
            window.Show(this);
        }

        private void SaveAlertToneSection(
            AlertSettingsPersistence persistence,
            IReadOnlyList<UserSettingsAlertToneConfig> configs)
        {
            try
            {
                if (!persistence.TryLoad(out UserSettingsAlertSection section))
                {
                    section = new UserSettingsAlertSection();
                }

                section.AlertTones = configs
                    .Where(config => config is not null
                        && !string.IsNullOrWhiteSpace(config.FilePath))
                    .Select(CloneAlertToneConfig)
                    .ToList();
                section.AlertToneFilePaths = section.AlertTones
                    .Select(config => config.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                section.AlertTonePositions = section.AlertTones
                    .GroupBy(config => config.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => ClonePosition(group.Last().Position),
                        StringComparer.OrdinalIgnoreCase);
                section.AlertToneTabs = section.AlertTones
                    .GroupBy(config => config.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last().TabName ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);
                persistence.Save(section);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Alert tone settings save failed", exception);
            }
        }

        private static UserSettingsAlertToneConfig CloneAlertToneConfig(
            UserSettingsAlertToneConfig config)
            => new()
            {
                Id = string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id,
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName)
                    ? GetAlertToneDisplayName(config.FilePath)
                    : config.DisplayName.Trim(),
                FilePath = config.FilePath.Trim(),
                TabName = string.IsNullOrWhiteSpace(config.TabName)
                    ? "Tab 1"
                    : config.TabName.Trim(),
                Position = ClonePosition(config.Position),
            };

        private static UserSettingsLayoutPosition ClonePosition(
            UserSettingsLayoutPosition? position)
            => new()
            {
                X = position?.X ?? 20,
                Y = position?.Y ?? 20,
            };

        private static string GetAlertToneDisplayName(string path)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(fileName) ? "Alert Tone" : fileName;
        }

        /// <summary>
        /// Opens one modeless generated-tone preset manager. The window and
        /// view-model emit request-only preview/send payloads; this shell owns
        /// persistence and target projection, while real playback/transmit
        /// dispatch remains a later gate.
        /// </summary>
        internal void OpenTonePresetManager()
        {
            if (tonePresetManagerWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            if (alertSettingsPersistence is not { } persistence)
            {
                return;
            }

            if (!persistence.TryLoad(out UserSettingsAlertSection section))
            {
                section = new UserSettingsAlertSection();
            }

            var manager = new TonePresetManagerViewModel(
                section.TonePresets ?? new List<UserSettingsTonePresetConfig>(),
                BuildTonePresetTargets());
            Action<IReadOnlyList<UserSettingsTonePresetConfig>> saveRequested =
                snapshot => SaveTonePresetSection(persistence, snapshot);
            Action<TonePresetRequest> previewRequested =
                request => _ = PreviewGeneratedToneAsync(request.Pcm);
            Action<TonePresetRequest> sendRequested =
                request => _ = SendGeneratedToneAsync(request.TargetResourceKey, request.Pcm);
            manager.SaveRequested += saveRequested;
            manager.PreviewRequested += previewRequested;
            manager.SendRequested += sendRequested;

            var window = new TonePresetManagerWindow(manager);
            tonePresetManagerWindow = window;
            window.Closed += (_, _) =>
            {
                manager.SaveRequested -= saveRequested;
                manager.PreviewRequested -= previewRequested;
                manager.SendRequested -= sendRequested;
                if (ReferenceEquals(tonePresetManagerWindow, window))
                {
                    tonePresetManagerWindow = null;
                }
            };
            window.Show(this);
        }

        private IReadOnlyList<TonePresetTarget> BuildTonePresetTargets()
        {
            if (codeplug?.Zones is not { } zones)
            {
                return Array.Empty<TonePresetTarget>();
            }

            return zones
                .Where(zone => zone?.Channels is not null)
                .SelectMany(zone => zone!.Channels!)
                .Where(channel => channel is not null
                    && !channel.RxOnly
                    && channel.GetChannelMode() != Codeplug.ChannelMode.NXDN
                    && !string.IsNullOrWhiteSpace(channel.System)
                    && !string.IsNullOrWhiteSpace(channel.Tgid))
                .GroupBy(channel => ResourceIdentity.Build(channel.System, channel.Tgid), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    Codeplug.Channel channel = group.First();
                    string system = channel.System?.Trim() ?? string.Empty;
                    string tgid = channel.Tgid?.Trim() ?? string.Empty;
                    return new TonePresetTarget(
                        group.Key,
                        $"{channel.Name?.Trim()} ({system} TG {tgid})");
                })
                .OrderBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SaveTonePresetSection(
            AlertSettingsPersistence persistence,
            IReadOnlyList<UserSettingsTonePresetConfig> configs)
        {
            try
            {
                if (!persistence.TryLoad(out UserSettingsAlertSection section))
                {
                    section = new UserSettingsAlertSection();
                }

                section.TonePresets = (configs ?? Array.Empty<UserSettingsTonePresetConfig>())
                    .Where(config => config is not null)
                    .Select(CloneTonePresetConfig)
                    .ToList();
                persistence.Save(section);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Tone preset settings save failed", exception);
            }
        }

        private static UserSettingsTonePresetConfig CloneTonePresetConfig(
            UserSettingsTonePresetConfig config)
        {
            var clone = new UserSettingsTonePresetConfig
            {
                Id = string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName)
                    ? "Tone Preset"
                    : config.DisplayName.Trim(),
                TargetResourceKey = config.TargetResourceKey?.Trim() ?? string.Empty,
            };
            clone.Steps = (config.Steps ?? new List<UserSettingsTonePresetStep>())
                .Where(step => step is not null)
                .Select(step => new UserSettingsTonePresetStep
                {
                    Kind = string.Equals(step.Kind, "hold", StringComparison.OrdinalIgnoreCase)
                        ? "hold"
                        : "tone",
                    FrequencyHz = step.FrequencyHz,
                    DurationSeconds = step.DurationSeconds,
                })
                .ToList();
            return clone;
        }

        /// <summary>
        /// Opens one modeless DTMF preset manager. The view model emits
        /// request-only PCM payloads; this shell owns persistence and target
        /// projection while real preview/transmit dispatch remains Gate 5.5.
        /// </summary>
        internal void OpenDtmfPresetManager()
        {
            if (dtmfPresetManagerWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            if (alertSettingsPersistence is not { } persistence)
            {
                return;
            }

            if (!persistence.TryLoad(out UserSettingsAlertSection section))
            {
                section = new UserSettingsAlertSection();
            }

            var manager = new DtmfPresetManagerViewModel(
                section.DtmfPresets ?? new List<UserSettingsDtmfPresetConfig>(),
                BuildDtmfPresetTargets());
            Action<IReadOnlyList<UserSettingsDtmfPresetConfig>> saveRequested =
                snapshot => SaveDtmfPresetSection(persistence, snapshot);
            Action<DtmfPresetRequest> previewRequested =
                request => _ = PreviewGeneratedToneAsync(request.Pcm);
            Action<DtmfPresetRequest> sendRequested =
                request => _ = SendGeneratedToneAsync(request.TargetResourceKey, request.Pcm);
            manager.SaveRequested += saveRequested;
            manager.PreviewRequested += previewRequested;
            manager.SendRequested += sendRequested;

            var window = new DtmfPresetManagerWindow(manager);
            dtmfPresetManagerWindow = window;
            window.Closed += (_, _) =>
            {
                manager.SaveRequested -= saveRequested;
                manager.PreviewRequested -= previewRequested;
                manager.SendRequested -= sendRequested;
                if (ReferenceEquals(dtmfPresetManagerWindow, window))
                {
                    dtmfPresetManagerWindow = null;
                }
            };
            window.Show(this);
        }

        private IReadOnlyList<DtmfPresetTarget> BuildDtmfPresetTargets()
            => BuildTonePresetTargets()
                .Select(target => new DtmfPresetTarget(target.Key, target.DisplayName))
                .ToList();

        private async Task PreviewGeneratedToneAsync(byte[] pcm)
        {
            if (toneDispatchCoordinator is not { } coordinator)
            {
                return;
            }

            await coordinator.PreviewGeneratedPcmAsync(pcm, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private async Task SendGeneratedToneAsync(string targetResourceKey, byte[] pcm)
        {
            if (toneDispatchCoordinator is not { } coordinator)
            {
                return;
            }

            var targets = ResolvePresetTarget(targetResourceKey);
            await coordinator.SendGeneratedPcmAsync(
                    pcm,
                    sendStartSignal: true,
                    cancellationToken: CancellationToken.None,
                    targetSnapshot: targets)
                .ConfigureAwait(false);
        }

        private IReadOnlyList<TransmitTarget> ResolvePresetTarget(string targetResourceKey)
        {
            if (transmitTargetResolver is not { } resolver)
            {
                return Array.Empty<TransmitTarget>();
            }

            var key = targetResourceKey?.Trim() ?? string.Empty;
            if (key.Length > 0)
            {
                int separator = key.IndexOf('|');
                if (separator > 0)
                {
                    var target = resolver.ResolveTalkgroup(
                        key[..separator],
                        key[(separator + 1)..]);
                    return target is { } resolved
                        ? new[] { resolved }
                        : Array.Empty<TransmitTarget>();
                }
            }

            return ResolveToneDispatchTargets();
        }

        private void SaveDtmfPresetSection(
            AlertSettingsPersistence persistence,
            IReadOnlyList<UserSettingsDtmfPresetConfig> configs)
        {
            try
            {
                if (!persistence.TryLoad(out UserSettingsAlertSection section))
                {
                    section = new UserSettingsAlertSection();
                }

                section.DtmfPresets = (configs ?? Array.Empty<UserSettingsDtmfPresetConfig>())
                    .Where(config => config is not null)
                    .Select(CloneDtmfPresetConfig)
                    .ToList();
                persistence.Save(section);
            }
            catch (Exception exception)
            {
                WriteApplicationException("DTMF preset settings save failed", exception);
            }
        }

        private static UserSettingsDtmfPresetConfig CloneDtmfPresetConfig(
            UserSettingsDtmfPresetConfig config)
        {
            var clone = new UserSettingsDtmfPresetConfig
            {
                Id = string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName)
                    ? "DTMF Preset"
                    : config.DisplayName.Trim(),
                TargetResourceKey = config.TargetResourceKey?.Trim() ?? string.Empty,
            };
            clone.Steps = (config.Steps ?? new List<UserSettingsDtmfPresetStep>())
                .Where(step => step is not null)
                .Select(step => new UserSettingsDtmfPresetStep
                {
                    Kind = string.Equals(step.Kind, "hold", StringComparison.OrdinalIgnoreCase)
                        ? "hold"
                        : "digit",
                    Digit = string.Equals(step.Kind, "hold", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : step.Digit?.Trim().ToUpperInvariant() ?? "1",
                    DurationSeconds = step.DurationSeconds,
                })
                .ToList();
            return clone;
        }

        /// <summary>
        /// Opens one modeless TAR Viewer instance owned by this dashboard.
        /// The recorder and WAVE player are shared application dependencies;
        /// reveal and confirmation stay injected shell adapters. Missing
        /// required dependencies are reported in the dashboard instead of
        /// silently disabling or failing the menu action.
        /// </summary>
        internal void OpenTarViewer()
        {
            if (tarRecorder is null || tarWaveFilePlayer is null)
            {
                string missing = tarRecorder is null && tarWaveFilePlayer is null
                    ? "recorder and WAVE player"
                    : tarRecorder is null
                        ? "recorder"
                        : "WAVE player";
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.TarViewerStatusMessage =
                        $"TAR Viewer unavailable: {missing} capability is not attached.";
                }

                return;
            }

            if (tarViewerWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            var viewer = new TarViewerWindow(
                new TarViewerViewModel(tarRecorder, tarViewerColumnPersistence),
                tarWaveFilePlayer,
                TarFileRevealService,
                TarConfirmationService);
            tarViewerWindow = viewer;
            viewer.Closed += (_, _) =>
            {
                if (ReferenceEquals(tarViewerWindow, viewer))
                {
                    tarViewerWindow = null;
                }
            };
            if (DataContext is MainWindowViewModel statusViewModel)
            {
                statusViewModel.TarViewerStatusMessage = string.Empty;
            }

            viewer.Show(this);
        }

        /// <summary>
        /// Opens the owner-bound Groups editor when a normalized codeplug and
        /// the shared groups settings adapter are available. The editor owns
        /// only presentation and request emission; this shell owns the save
        /// request and forwards PTT/runtime requests to the coordinator
        /// composed by the owner window.
        /// </summary>
        internal void OpenPatchGroups()
        {
            if (patchGroupsWindow is { } existing)
            {
                existing.Activate();
                return;
            }

            if (codeplug is null
                || groupsPersistence is null
                || DataContext is not MainWindowViewModel viewModel
                || codeplug.Groups is not { Count: > 0 } definitions)
            {
                return;
            }

            var channels = (codeplug.Zones ?? new List<Codeplug.Zone>())
                .Where(zone => zone?.Channels is not null)
                .SelectMany(zone => zone!.Channels!)
                .Where(channel => channel is not null)
                .ToList();
            var editor = new PatchGroupsViewModel(
                definitions,
                channels,
                groupsPersistence,
                membershipContextKey: groupsMembershipContextKey,
                retainPatchStateOnStartup: viewModel.Preferences?.RetainPatchStateOnStartup == true);

            Action<string, bool, IReadOnlyList<PatchTalkgroupMember>> pttRequested =
                (groupName, isActive, members) => OnPatchPttRequested(groupName, isActive, members);
            editor.PttRequested += pttRequested;

            Action<UserSettingsGroupSection> saveRequested = section =>
            {
                try
                {
                    viewModel.ApplyGroupsSection(
                        section,
                        groupsMembershipContextKey,
                        viewModel.Preferences?.RetainPatchStateOnStartup == true);
                    patchForwardingCoordinator?.ApplySavedMemberships(section);
                    groupsPersistence.Save(section);
                }
                catch (Exception exception)
                {
                    WriteApplicationException("Groups settings save failed", exception);
                }
            };
            editor.SaveRequested += saveRequested;

            var window = new PatchGroupsWindow(editor);
            patchGroupsWindow = window;
            window.Closed += (_, _) =>
            {
                editor.SaveRequested -= saveRequested;
                editor.PttRequested -= pttRequested;
                if (ReferenceEquals(patchGroupsWindow, window))
                {
                    patchGroupsWindow = null;
                }
            };
            window.Show(this);
        }

        private void SelectAllChannels_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ToggleSelectAllCurrentZone();
            }
        }

        private void PageSelect_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ChannelSlotViewModel slot })
            {
                slot.RequestPageSelect();
            }
        }

        private void Marker_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ChannelSlotViewModel slot })
            {
                slot.RequestMarker();
            }
        }

        private void AlertTone1_Click(object? sender, RoutedEventArgs e)
            => SendAlertToolbarTone("alert1.wav");

        private void AlertTone2_Click(object? sender, RoutedEventArgs e)
            => SendAlertToolbarTone("alert2.wav");

        private void AlertTone3_Click(object? sender, RoutedEventArgs e)
            => SendAlertToolbarTone("alert3.wav");

        private void SendAlertToolbarTone(string fileName)
        {
            if (toneDispatchCoordinator is not { } coordinator)
            {
                return;
            }

            string path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Audio",
                fileName);
            _ = coordinator.ReadAndSendWaveFileAsync(
                path,
                sendStartSignal: true,
                CancellationToken.None,
                ResolveToneDispatchTargets());
        }

        /// <summary>
        /// Thin pointer-press wiring for the channel-card template. Filters
        /// to left-button presses on the card <see cref="Border"/>, translates
        /// the press through
        /// <see cref="ChannelCardPointerInterpreter.TryGetChannelClick"/>,
        /// and forwards accepted clicks to the dashboard view-model. Safe
        /// no-op for any other sender, null data context, or non-slot data
        /// context.
        /// </summary>
        private void ChannelCard_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            {
                return;
            }

            if (sender is not Border card || e.Source is Button)
            {
                return;
            }

            if (!ChannelCardPointerInterpreter.TryGetChannelClick(
                    card.DataContext,
                    e.KeyModifiers,
                    out var slotNumber,
                    out var setPrimary))
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ProcessChannelClick(slotNumber, setPrimary);
            }
        }

        /// <summary>
        /// Starts the independent momentary PTT lifecycle for the channel-card
        /// button that received the pointer press. Pointer capture is held by
        /// the button so release outside the card still reaches the release
        /// path; the coordinator resolves only this slot's target.
        /// </summary>
        private void ChannelPttButton_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!PttButtonPointerInterpreter.TryGetPttPointerAction(
                    e.Properties.PointerUpdateKind,
                    out var isDown)
                || !isDown
                || sender is not Button { DataContext: ChannelSlotViewModel slot } button)
            {
                return;
            }

            e.Pointer.Capture(button);
            _ = HandleChannelPttDownAsync(slot);
        }

        /// <summary>Releases the active card capture on a left-button release.</summary>
        private void ChannelPttButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!PttButtonPointerInterpreter.TryGetPttPointerAction(
                    e.Properties.PointerUpdateKind,
                    out var isDown)
                || isDown)
            {
                return;
            }

            e.Pointer.Capture(null);
            _ = HandleChannelPttUpAsync();
        }

        /// <summary>
        /// Pointer capture loss is an unconditional release. The coordinator
        /// makes the redundant release idempotent and keeps the card visual
        /// asserted until router teardown has completed.
        /// </summary>
        private void ChannelPttButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => _ = HandleChannelPttUpAsync();

        private async Task HandleChannelPttDownAsync(ChannelSlotViewModel slot)
        {
            if (channelPttRuntimeCoordinator is not { } coordinator)
            {
                return;
            }

            try
            {
                await channelPttRuntimeCoordinator.HandlePointerDownAsync(slot)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Channel PTT request failed", exception);
            }
        }

        private async Task HandleChannelPttUpAsync()
        {
            if (channelPttRuntimeCoordinator is not { } coordinator)
            {
                return;
            }

            try
            {
                await channelPttRuntimeCoordinator.HandlePointerUpAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Channel PTT release failed", exception);
            }
        }

        /// <summary>
        /// Thin pointer-press wiring for the dashboard PUSH TO TALK button.
        /// Translates the pointer update through
        /// <see cref="PttButtonPointerInterpreter.TryGetPttPointerAction"/>
        /// and forwards an accepted left-button press to the PTT capability
        /// slice. Safe no-op for any other pointer update, null data
        /// context, or absent slice.
        /// </summary>
        private void PttButton_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!PttButtonPointerInterpreter.TryGetPttPointerAction(
                    e.Properties.PointerUpdateKind, out var isDown)
                || !isDown)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Ptt?.PttPointerDown();
            }
        }

        /// <summary>
        /// Thin pointer-release wiring for the dashboard PUSH TO TALK
        /// button. Translates the pointer update through
        /// <see cref="PttButtonPointerInterpreter.TryGetPttPointerAction"/>
        /// and forwards an accepted left-button release to the PTT
        /// capability slice. Safe no-op for any other pointer update, null
        /// data context, or absent slice.
        /// </summary>
        private void PttButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!PttButtonPointerInterpreter.TryGetPttPointerAction(
                    e.Properties.PointerUpdateKind, out var isDown)
                || isDown)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Ptt?.PttPointerUp();
            }
        }

        /// <summary>
        /// Thin pointer-capture-loss wiring for the dashboard PUSH TO TALK
        /// button. Forwards an unconditional release to the PTT capability
        /// slice so engagement can never stick when pointer capture is lost;
        /// the redundant release is intentional and idempotent. Safe no-op
        /// when the view-model or slice is absent.
        /// </summary>
        private void PttButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Ptt?.PttPointerUp();
            }
        }

        private void WebStreamToggle_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: WebStreamShellItemViewModel item })
                _ = item.ToggleAsync();
        }

        private void WebStreamCard_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border { DataContext: WebStreamShellItemViewModel item } card
                || e.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
                || !ReferenceEquals(e.Source, card)
                || layoutSection?.LockWidgets == true)
            {
                return;
            }

            draggedWebStream = item;
            webStreamDragStart = e.GetPosition(this);
            webStreamPositionStart = item.Position;
            e.Pointer.Capture(card);
        }

        private void WebStreamCard_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (layoutSection?.LockWidgets == true)
            {
                e.Pointer.Capture(null);
                draggedWebStream = null;
                return;
            }

            if (draggedWebStream is not { } item
                || sender is not Border { DataContext: WebStreamShellItemViewModel senderItem }
                || !ReferenceEquals(item, senderItem))
            {
                return;
            }

            var current = e.GetPosition(this);
            item.SetPosition(
                webStreamPositionStart.X + current.X - webStreamDragStart.X,
                webStreamPositionStart.Y + current.Y - webStreamDragStart.Y);
        }

        private void WebStreamCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            e.Pointer.Capture(null);
            draggedWebStream = null;
        }

        /// <summary>
        /// Thin click wiring for the FNE system-card Start/Stop toggle
        /// button. Resolves the row from the sender button's data
        /// context and forwards a Stop request when the row reports a
        /// connection, otherwise a Start request, to the window
        /// view-model's FNE connection manager. Safe no-op for any other
        /// sender, null data context, or non-row data context. This shell
        /// never touches fnecore or the network; it only forwards
        /// requests.
        /// </summary>
        private void FneStartStop_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.DataContext is not FneSystemConnectionViewModel row
                || DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            if (row.IsConnected)
            {
                viewModel.FneConnections.StopSystem(row.SystemName);
            }
            else
            {
                viewModel.FneConnections.StartSystem(row.SystemName);
            }
        }

        /// <summary>
        /// Thin click wiring for the FNE system-card Restart button.
        /// Resolves the row from the sender button's data context and
        /// forwards a Restart request to the window view-model's FNE
        /// connection manager. Safe no-op for any other sender, null data
        /// context, or non-row data context.
        /// </summary>
        private void FneRestart_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.DataContext is not FneSystemConnectionViewModel row
                || DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            viewModel.FneConnections.RestartSystem(row.SystemName);
        }

        /// <summary>
        /// Routes a global hotkey event from the injected service onto
        /// the UI thread and forwards it to the PTT capability slice.
        /// The event may arrive on any thread the service raises from;
        /// the unconditional post guarantees the slice is only ever
        /// touched on the UI thread. Safe no-op when the view-model or
        /// the PTT slice is absent; the slice itself ignores gestures
        /// that are not configured.
        /// </summary>
        private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
            => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel vm && vm.Ptt is { } ptt)
                {
                    ptt.ApplyHotkeyPress(e.Gesture, e.EventType);
                }
            });

        /// <summary>
        /// Relays a coordinator registration result onto the UI thread so
        /// the view-model remains free of dispatcher and service concerns.
        /// </summary>
        private void OnHotkeyRegistrationStatusChanged(
            HotkeyRegistrationStatus status,
            HotkeyGesture gesture)
            => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.ReportPttHotkeyStatus(status, gesture);
                }
            });

        /// <summary>
        /// Drives the PTT key-up watchdog once per timer tick: probes
        /// the currently configured hotkey gesture's physical key state
        /// through the injected reader and forwards the result to the
        /// PTT capability slice. Ticks with no PTT slice or no
        /// configured gesture are skipped. A throwing probe never
        /// force-releases PTT — the tick is skipped and a diagnostic is
        /// written to the debug output only.
        /// </summary>
        private void OnWatchdogTick(object? sender, EventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.Ptt is not { } ptt
                || ptt.Hotkey is not { } gesture)
            {
                return;
            }

            try
            {
                ptt.WatchdogTick(keyStateReader!.IsKeyDown(gesture));
            }
            catch (Exception ex)
            {
                WriteApplicationException("PTT key-state probe failed; watchdog tick skipped", ex);
            }
        }

        /// <summary>
        /// Stops and detaches the PTT key-up watchdog timer and tears
        /// down the headless FNE slice, the receive glue and the
        /// talkgroup audio router
        /// when the window closes: the bridge detaches first (stopping
        /// event flow in both directions), then the service disconnects
        /// and disposes every transport and cancels all schedulers, the
        /// glue detaches its routing delegate so late frames are
        /// dropped, and
        /// finally the router stops every audio pipeline and disposes
        /// the shared factory. All disposals are idempotent, so a
        /// repeated close event is harmless.
        /// </summary>
        internal Task DisposeRuntimeAsync()
        {
            lock (runtimeDisposeGate)
            {
                runtimeDisposeTask ??= DisposeRuntimeCoreAsync();
                return runtimeDisposeTask;
            }
        }

        private async Task DisposeRuntimeCoreAsync()
        {
            try
            {
                if (!suppressRuntimeSettingsPersistence)
                {
                    SaveWebStreamSettings();
                    SaveLayoutSettings();
                }

            if (hotkeys is not null)
            {
                hotkeys.HotkeyPressed -= OnHotkeyPressed;
            }

            hotkeyRegistrationCoordinator?.Dispose();

            patchGroupsWindow?.Close();
            patchGroupsWindow = null;
            alertToneManagerWindow?.Close();
            alertToneManagerWindow = null;
            tonePresetManagerWindow?.Close();
            tonePresetManagerWindow = null;
            dtmfPresetManagerWindow?.Close();
            dtmfPresetManagerWindow = null;
            tarViewerWindow?.Close();
            tarViewerWindow = null;
            tarRetentionTimer?.Stop();

            if (macAudioDeviceCatalog is { } macCatalog)
            {
                macCatalog.DevicesChanged -= OnAudioDevicesChanged;
            }

            if (watchdogTimer is { } timer)
            {
                timer.Tick -= OnWatchdogTick;
                timer.Stop();
            }

            if (receiveProjectionTimer is { } receiveTimer)
            {
                receiveTimer.Tick -= OnReceiveProjectionTimerTick;
                receiveTimer.Stop();
            }

            if (receiveProjectionViewModel is { } receiveViewModel)
            {
                receiveViewModel.PropertyChanged -= MainWindowViewModel_PropertyChanged;
                foreach (var system in receiveViewModel.FneConnections.Systems)
                {
                    system.PropertyChanged -= FneSystemConnection_PropertyChanged;
                }
            }

            receiveProjection?.Dispose();
            receiveProjection = null;
            receiveProjectionViewModel = null;

            fneConnectionBridge?.Dispose();
            patchForwardingCoordinator?.Dispose();
            fneConnectionService?.Dispose();
            fneReceiveGlue?.Dispose();

            if (DataContext is MainWindowViewModel audioViewModel)
            {
                audioViewModel.ChannelSelectionChanged -= OnChannelSelectionChanged;
                audioViewModel.ChannelVolumeChanged -= OnChannelVolumeChanged;
                audioViewModel.ChannelOutputDeviceChanged -= OnChannelOutputDeviceChanged;
            }

                if (talkgroupAudioRouter is { } router)
                {
                    await DisposeWebStreamsThenRouterAsync(router).ConfigureAwait(false);
                }
                else
                {
                    await DisposeWebStreamsAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                debugLogWindow?.Close();
                debugLogWindow = null;

                if (ownsRuntimeServices && diagnosticLogSink is { } diagnosticSink)
                {
                    fnecoreTransportFactory?.ClearDiagnosticWriter();
                    diagnosticLogSink = null;
                }

                if (ownsRuntimeServices && hotkeys is not null)
                {
                    hotkeys.Dispose();
                }

                if (ownsRuntimeServices && macAudioDeviceCatalog is not null)
                {
                    await macAudioDeviceCatalog.DisposeAsync().ConfigureAwait(false);
                    macAudioDeviceCatalog = null;
                }

                userBackgroundBitmap?.Dispose();
                userBackgroundBitmap = null;
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _ = ObserveAsync(
                DisposeRuntimeAsync(),
                "Window runtime shutdown failed");
        }

        private void SaveWebStreamSettings()
        {
            if (webStreamShell is not { } shell)
                return;

            var snapshot = shell.Snapshot();
            if (restoreSettingsPersistence is { } restorePersistence)
            {
                try
                {
                    if (!restorePersistence.TryLoad(out UserSettingsRestoreSection section))
                        section = new UserSettingsRestoreSection();

                    section.SelectedWebStreams =
                        DataContext is MainWindowViewModel viewModel
                        && viewModel.Preferences?.RestoreSelectedChannelsOnStartup == true
                            ? snapshot.SelectedNames.ToList()
                            : new List<string>();
                    restorePersistence.Save(section);
                }
                catch (Exception exception)
                {
                    WriteApplicationException("Web-stream restore save failed", exception);
                }
            }

            if (audioSettingsPersistence is { } audioPersistence)
            {
                try
                {
                    if (!audioPersistence.TryLoad(out UserSettingsAudioSection section))
                        section = new UserSettingsAudioSection();
                    section.WebStreamVolumes = new Dictionary<string, double>(
                        snapshot.Volumes,
                        StringComparer.OrdinalIgnoreCase);
                    audioPersistence.Save(section);
                }
                catch (Exception exception)
                {
                    WriteApplicationException("Web-stream volume save failed", exception);
                }
            }

            if (layoutSection is { } layout)
            {
                layout.WebStreamPositions = new Dictionary<string, UserSettingsLayoutPosition>(
                    snapshot.Positions,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        private async Task DisposeWebStreamsThenRouterAsync(TalkgroupAudioRouter router)
        {
            try
            {
                await DisposeWebStreamsAsync().ConfigureAwait(false);
            }
            finally
            {
                await DisposeRouterAndFlushRecordingsAsync(router).ConfigureAwait(false);
            }
        }

        private async Task DisposeWebStreamsAsync()
        {
            try
            {
                if (webStreamShell is { } shell)
                {
                    await shell.DisposeAsync().ConfigureAwait(false);
                    webStreamShell = null;
                }
            }
            finally
            {
                if (webStreamSourceFactory is { } sourceFactory)
                {
                    sourceFactory.Dispose();
                    webStreamSourceFactory = null;
                }
                if (talkgroupAudioRouter is null)
                {
                    tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                    tarRecordingCoordinator?.Dispose();
                }
            }
        }

        private async Task ObserveAsync(Task operation, string description)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                WriteApplicationException(description, exception);
            }
        }

        private void WriteApplicationException(string context, Exception exception)
        {
            if (diagnosticLogSink is { } sink)
                sink.WriteException(LogLevel.ERROR, context, exception);
            else
                System.Diagnostics.Debug.WriteLine($"{context}: {exception}");
        }

        private void SaveLayoutSettings()
        {
            if (!layoutHydrated
                || layoutPersistence is null
                || layoutSection is null)
            {
                return;
            }

            layoutSection.WindowWidth = Width;
            layoutSection.WindowHeight = Height;
            layoutSection.Maximized = WindowState == WindowState.Maximized;
            layoutSection.KeepWindowOnTop = Topmost;
            SaveLayoutSection();
            layoutHydrated = false;
        }

        private void SaveLayoutSection()
        {
            if (!layoutHydrated
                || layoutPersistence is null
                || layoutSection is null)
            {
                return;
            }

            layoutSection.WindowWidth = Width;
            layoutSection.WindowHeight = Height;
            layoutSection.Maximized = WindowState == WindowState.Maximized;
            layoutSection.KeepWindowOnTop = Topmost;
            try
            {
                layoutPersistence.Save(layoutSection);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Layout settings save failed", exception);
            }
        }

        private async Task DisposeRouterAndFlushRecordingsAsync(TalkgroupAudioRouter router)
        {
            try
            {
                if (toneDispatchCoordinator is { } toneDispatch)
                {
                    await toneDispatch.DisposeAsync().ConfigureAwait(false);
                    toneDispatchCoordinator = null;
                }

                if (channelPttRuntimeCoordinator is { } channelPtt)
                {
                    await channelPttRuntimeCoordinator.DisposeAsync().ConfigureAwait(false);
                    channelPttRuntimeCoordinator = null;
                }

                if (patchPttRuntimeCoordinator is { } patchPtt)
                {
                    await patchPtt.DisposeAsync().ConfigureAwait(false);
                    patchPttRuntimeCoordinator = null;
                }

                await router.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                tarRecordingCoordinator?.Dispose();
            }
        }

        private void OnAudioDevicesChanged(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.AudioSettings?.Refresh();
                }

                _ = talkgroupAudioRouter?.RequestCaptureRestartAsync();
            });
        }

        private void OnPatchPttRequested(
            string groupName,
            bool isActive,
            IReadOnlyList<PatchTalkgroupMember> members)
        {
            _ = HandlePatchPttRequestAsync(groupName, isActive, members);
        }

        private async Task HandlePatchPttRequestAsync(
            string groupName,
            bool isActive,
            IReadOnlyList<PatchTalkgroupMember> members)
        {
            if (patchPttRuntimeCoordinator is not { } coordinator)
            {
                return;
            }

            try
            {
                await coordinator.HandleRequestAsync(groupName, isActive, members)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                WriteApplicationException("Patch PTT request failed", exception);
            }
        }

        private async Task BeginChannelTransmitAsync(
            TalkgroupAudioRouter router,
            IReadOnlyList<TransmitTarget> targets,
            MainWindowViewModel viewModel)
        {
            if (targets.Count != 1)
            {
                throw new InvalidOperationException("Channel PTT requires exactly one transmit target.");
            }

            var target = targets[0];
            try
            {
                tarRecordingCoordinator?.TryStartTransmit(
                    target,
                    transmitTargetResolver?.ResolveChannelName(target) ?? target.TalkgroupId,
                    DateTime.UtcNow,
                    out _);

                await router.BeginTransmitAsync(
                    targets,
                    viewModel.AudioSettings?.SelectedInputId ?? AudioDeviceId.Default,
                    CancellationToken.None).ConfigureAwait(false);

                if (viewModel.Preferences?.TalkPermitTone == true)
                {
                    try
                    {
                        await router.PlayLocalPcmAsync(
                            TonePcmGenerator.GenerateTalkPermitTone()).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        WriteApplicationException("Channel PTT permit tone failed", exception);
                    }
                }
            }
            catch
            {
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                throw;
            }
        }

        private async Task EndChannelTransmitAsync(TalkgroupAudioRouter router)
        {
            try
            {
                await router.EndTransmitAsync().ConfigureAwait(false);
            }
            finally
            {
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
            }
        }

        private async Task BeginPatchTransmitAsync(
            TalkgroupAudioRouter router,
            IReadOnlyList<TransmitTarget> targets,
            MainWindowViewModel viewModel)
        {
            try
            {
                foreach (var target in targets)
                {
                    tarRecordingCoordinator?.TryStartTransmit(
                        target,
                        transmitTargetResolver?.ResolveChannelName(target) ?? target.TalkgroupId,
                        DateTime.UtcNow,
                        out _);
                }

                await router.BeginTransmitAsync(
                    targets,
                    viewModel.AudioSettings?.SelectedInputId ?? AudioDeviceId.Default,
                    CancellationToken.None).ConfigureAwait(false);
                if (viewModel.Preferences?.TalkPermitTone == true)
                {
                    await router.PlayLocalPcmAsync(
                        TonePcmGenerator.GenerateTalkPermitTone()).ConfigureAwait(false);
                }
            }
            catch
            {
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                throw;
            }
        }

        private bool ShouldMuteRxPlayback()
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.Preferences?.MuteRxAudioWhileTransmitting != true)
            {
                return false;
            }

            return viewModel.Ptt?.IsEngaged == true
                || Volatile.Read(ref dashboardTransmitActive) != 0
                || patchPttRuntimeCoordinator?.IsTransmitActive == true
                || channelPttRuntimeCoordinator?.IsTransmitActive == true;
        }

        /// <summary>
        /// Routes a PTT engagement change from the view-model's PTT slice
        /// to the talkgroup audio router: engage begins a transmit for
        /// the resolved targets (the PTT slice's press-time
        /// <see cref="PttCapabilityViewModel.EngagedTargets"/> snapshot
        /// fanned out through the resolver, or the primary channel when
        /// the PTT slice is absent), release ends it. A press with no
        /// resolved target (channel assignment has not landed) is a
        /// no-op. The event may arrive on the UI thread only (the PTT
        /// slice is UI-thread driven); the router itself is thread-safe
        /// either way.
        /// </summary>
        /// <remarks>
        /// Deliberate deviation from the WPF single-channel PTT path: one
        /// capture serves all engaged targets, so a release ends every
        /// target together — there is no per-target release in the
        /// AllChannels mode (RED-pinned fan-out contract).
        /// </remarks>
        private void OnPttStateRequested(bool isDown)
        {
            if (talkgroupAudioRouter is not { } router)
            {
                return;
            }

            var viewModel = DataContext as MainWindowViewModel;

            if (isDown)
            {
                if (patchPttRuntimeCoordinator?.IsTransmitActive == true)
                {
                    rejectDashboardPttRelease = true;
                    if (viewModel?.Ptt is { } activePtt)
                    {
                        if (activePtt.ToggleMode)
                        {
                            activePtt.PttPointerDown();
                        }
                        else
                        {
                            activePtt.PttPointerUp();
                        }
                    }

                    if (viewModel is not null)
                    {
                        viewModel.AudioStatusMessage = "PTT blocked: patch PTT is active.";
                    }

                    return;
                }

                var targets = ResolveTransmitTargets();
                if (targets.Count == 0)
                {
                    return;
                }

                var unavailableTarget = targets.FirstOrDefault(
                    target => !IsTransmitTargetAvailable(target));
                if (string.IsNullOrWhiteSpace(unavailableTarget.SystemName))
                {
                    var inputDeviceId = viewModel?.AudioSettings?.SelectedInputId
                        ?? AudioDeviceId.Default;
                    Interlocked.Exchange(ref dashboardTransmitActive, 1);
                    if (viewModel?.Preferences?.MuteRxAudioWhileTransmitting == true)
                    {
                        router.ClearAllTalkgroupBuffers();
                    }
                    foreach (var target in targets)
                    {
                        tarRecordingCoordinator?.TryStartTransmit(
                            target,
                            transmitTargetResolver?.ResolveChannelName(target) ?? target.TalkgroupId,
                            DateTime.UtcNow,
                            out _);
                    }
                    _ = BeginTransmitAndPlayPermitToneAsync(
                        router,
                        targets,
                        inputDeviceId,
                        viewModel?.Preferences?.TalkPermitTone == true);
                    return;
                }

                viewModel?.Ptt?.CancelEngagement();
                if (viewModel is not null)
                {
                    viewModel.AudioStatusMessage = "Target TG unavailable on FNE";
                }
                return;
            }
            else
            {
                if (rejectDashboardPttRelease)
                {
                    rejectDashboardPttRelease = false;
                    return;
                }

                _ = EndTransmitAndStopRecordingAsync(router);
            }
        }

        private async Task BeginTransmitAndPlayPermitToneAsync(
            TalkgroupAudioRouter router,
            IReadOnlyList<TransmitTarget> targets,
            AudioDeviceId inputDeviceId,
            bool playPermitTone)
        {
            try
            {
                await router.BeginTransmitAsync(
                    targets,
                    inputDeviceId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref dashboardTransmitActive, 0);
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                WriteApplicationException("PTT audio start failed", exception);
                return;
            }

            if (playPermitTone)
            {
                await router.PlayLocalPcmAsync(
                    TonePcmGenerator.GenerateTalkPermitTone()).ConfigureAwait(false);
            }
        }

        private async Task EndTransmitAndStopRecordingAsync(TalkgroupAudioRouter router)
        {
            try
            {
                await router.EndTransmitAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref dashboardTransmitActive, 0);
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Resolves the transmit targets for a PTT press: the PTT
        /// view-model's press-time
        /// <see cref="PttCapabilityViewModel.EngagedTargets"/> snapshot
        /// projected onto codeplug channel names and resolved through the
        /// composed <see cref="TransmitTargetResolver"/> (order-preserving;
        /// unresolvable slots are skipped, an all-unresolvable snapshot
        /// yields an empty list). When the PTT slice is absent, the
        /// primary channel is the fallback target (the pre-fan-out
        /// behavior). Returns an empty list — making the PTT press a
        /// no-op, the audio router untouched — when no resolver was
        /// composed (no codeplug), neither the PTT slice nor a primary
        /// channel is available, or nothing resolves to a transmittable
        /// target.
        /// </summary>
        private IReadOnlyList<TransmitTarget> ResolveTransmitTargets()
        {
            if (transmitTargetResolver is not { } resolver
                || DataContext is not MainWindowViewModel vm)
            {
                return Array.Empty<TransmitTarget>();
            }

            if (vm.Ptt is { } ptt)
            {
                // The press-time snapshot is non-null whenever the PTT
                // slice raised a true engagement (engagement is a no-op
                // without targets); a null snapshot resolves to no
                // targets.
                return resolver.ResolveAll(ptt.EngagedTargets?.Select(slot => slot.ChannelName));
            }

            return vm.PrimaryChannel?.ChannelName is { } name
                ? resolver.Resolve(name) is { } fallback
                    ? new[] { fallback }
                    : Array.Empty<TransmitTarget>()
                : Array.Empty<TransmitTarget>();
        }

        private IReadOnlyList<TransmitTarget> ResolveToneDispatchTargets()
        {
            if (transmitTargetResolver is not { } resolver
                || DataContext is not MainWindowViewModel viewModel)
            {
                return Array.Empty<TransmitTarget>();
            }

            var selectedSlots = viewModel.SelectedChannels
                .Where(slot => slot.IsSelected && !slot.IsRxOnly)
                .ToList();

            var pageSelectedTargets = resolver.ResolveAll(
                selectedSlots
                    .Where(slot => slot.PageState)
                    .Select(slot => slot.ChannelName));
            if (pageSelectedTargets.Count > 0)
            {
                return pageSelectedTargets;
            }

            var targets = resolver.ResolveAll(selectedSlots.Select(slot => slot.ChannelName));
            if (targets.Count > 0)
            {
                return targets;
            }

            return viewModel.PrimaryChannel?.ChannelName is { } primary
                && resolver.Resolve(primary) is { } fallback
                ? new[] { fallback }
                : Array.Empty<TransmitTarget>();
        }

        /// <summary>
        /// Marshals a capture-end notification from the talkgroup audio
        /// router onto the UI thread as the view-model's
        /// <see cref="MainWindowViewModel.AudioStatusMessage"/>.
        /// </summary>
        private void OnCaptureEnded(AudioStreamEnd end)
            => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.AudioStatusMessage = $"PTT capture ended: {end.StopReason}";
                }
            });

        /// <summary>
        /// Marshals a monitor-stream-end notification (per-talkgroup or
        /// transmit-loopback device loss) from the talkgroup audio router
        /// onto the UI thread as the view-model's
        /// <see cref="MainWindowViewModel.AudioStatusMessage"/>.
        /// </summary>
        private void OnMonitorStreamEnded()
            => Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.AudioStatusMessage = "Monitor stream ended: output device lost";
                }
            });

        /// <summary>
        /// Closes an RX TAR session when the router's idle release fires and
        /// no explicit terminator was classified by the receive glue.
        /// </summary>
        private void OnTalkgroupStreamEnded(string key, VoiceMode mode)
        {
            receiveProjection?.Clear(key);
            patchForwardingCoordinator?.HandleStreamEnded(key, mode);
            tarRecordingCoordinator?.EndReceive(key, mode, DateTime.UtcNow);
        }

        /// <summary>
        /// Thin click wiring for the Set hotkey button: begins window-local
        /// hotkey capture on the capture slice. Safe no-op when the
        /// view-model or capture slice is absent; the slice itself ignores
        /// repeated starts while already capturing.
        /// </summary>
        private void HotkeyCapture_Start_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.HotkeyCapture?.StartCapture();
            }
        }

        /// <summary>
        /// Thin click wiring for the Clear button: forwards a clear
        /// request to the capture slice, which always clears the PTT
        /// hotkey and cancels capture. Safe no-op when the view-model or
        /// capture slice is absent.
        /// </summary>
        private void HotkeyCapture_Clear_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.HotkeyCapture?.ClearHotkey();
            }
        }

        /// <summary>
        /// Thin click wiring for the Cancel button: cancels an in-progress
        /// capture on the capture slice. Safe no-op when the view-model or
        /// capture slice is absent; the slice itself ignores cancels while
        /// idle.
        /// </summary>
        private void HotkeyCapture_Cancel_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.HotkeyCapture?.Cancel();
            }
        }

        /// <summary>
        /// Window-local hotkey capture: while the capture slice reports
        /// <see cref="HotkeyCaptureViewModel.IsCapturing"/>, every key
        /// event reaching the window is translated with
        /// <see cref="KeyGestureMapper.TryMap"/> and applied to the
        /// slice, which stores the gesture on the PTT capability and
        /// exits capture. Unsupported keys leave capture active and the
        /// event unhandled; supported keys mark the event handled so the
        /// capture consumes it. Escape is a supported gesture (it maps
        /// to <see cref="HotkeyKey.Escape"/>) and is intentionally not
        /// special-cased. This is window-local routing only — no
        /// registration, persistence, or native behavior is performed
        /// here. Safe no-op when the view-model or capture slice is
        /// absent or capture is idle.
        /// </summary>
        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel
                || viewModel.HotkeyCapture is not { } capture
                || !capture.IsCapturing)
            {
                return;
            }

            if (KeyGestureMapper.TryMap(e.Key, e.KeyModifiers, out var gesture))
            {
                capture.ApplyKey(gesture);
                e.Handled = true;
            }
        }
    }
}
