// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using DvmConsole.Core.Networking;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;

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
        private readonly DispatcherTimer? watchdogTimer = null;

        /// <summary>
        /// Hourly TAR retention maintenance timer; null when recording is
        /// not composed or after the window closes.
        /// </summary>
        private readonly DispatcherTimer? tarRetentionTimer = null;

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
        /// the glue's disposed state.
        /// </summary>
        private FneReceiveGlue? fneReceiveGlue = null;

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
        private TarViewerWindow? tarViewerWindow;

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
        {
            InitializeComponent();
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
            }

            // Compose the receive glue independently of audio. Call
            // history and connection status must keep working when the
            // host has no audio catalog (non-macOS tests, a CoreAudio
            // permission failure, or a headless session). The optional
            // router is resolved at event time, so frames are still
            // classified and recorded while audio stays dormant.
            fneReceiveGlue = new FneReceiveGlue(
                (key, frame, mode) => talkgroupAudioRouter?.RouteVoiceFrame(key, frame, mode));

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
                    decodedPcmObserver: tarRecordingCoordinator,
                    transmittedPcmObserver: tarRecordingCoordinator,
                    resolveMonitorEnabled: audioViewModel.IsMonitorEnabled,
                    resolveTalkgroupOutputDevice: audioViewModel.ResolveMonitorOutputDevice,
                    resolveTalkgroupVolume: audioViewModel.ResolveMonitorVolume);

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

                if (catalog is MacAudioDeviceCatalog macCatalog)
                {
                    macAudioDeviceCatalog = macCatalog;
                    macCatalog.DevicesChanged += OnAudioDevicesChanged;
                }
            }

            if (tarRecordingCoordinator is { } recordingCoordinator)
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

            if (DataContext is MainWindowViewModel viewModelWithSettings
                && viewModelWithSettings.AudioSettings is { } settings)
            {
                settings.PropertyChanged += AudioSettings_PropertyChanged;
                ApplyAudioSelections();
            }

            Closed += OnWindowClosed;

            if (keyStateReader is null)
            {
                return;
            }

            this.keyStateReader = keyStateReader;

            watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            watchdogTimer.Tick += OnWatchdogTick;
            watchdogTimer.Start();
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

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += OnReceiveProjectionTimerTick;
            timer.Start();
            receiveProjectionTimer = timer;
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

        private void SelectAllChannels_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ToggleSelectAllCurrentZone();
            }
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

            if (sender is not Border card)
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
                System.Diagnostics.Debug.WriteLine(
                    $"PTT key-state probe failed; watchdog tick skipped: {ex}");
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
        private void OnWindowClosed(object? sender, EventArgs e)
        {
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
                _ = DisposeRouterAndFlushRecordingsAsync(router);
            }
            else
            {
                tarRecordingCoordinator?.StopAllTransmit(DateTime.UtcNow);
                tarRecordingCoordinator?.Dispose();
            }
        }

        private async Task DisposeRouterAndFlushRecordingsAsync(TalkgroupAudioRouter router)
        {
            try
            {
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
            Dispatcher.UIThread.Post(() => _ = talkgroupAudioRouter?.RequestCaptureRestartAsync());
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

            if (isDown)
            {
                var targets = ResolveTransmitTargets();
                if (targets.Count == 0)
                {
                    return;
                }

                var inputDeviceId = (DataContext as MainWindowViewModel)?.AudioSettings?.SelectedInputId
                    ?? AudioDeviceId.Default;
                foreach (var target in targets)
                {
                    tarRecordingCoordinator?.TryStartTransmit(
                        target,
                        transmitTargetResolver?.ResolveChannelName(target) ?? target.TalkgroupId,
                        DateTime.UtcNow,
                        out _);
                }
                _ = router.BeginTransmitAsync(targets, inputDeviceId, CancellationToken.None);
            }
            else
            {
                _ = EndTransmitAndStopRecordingAsync(router);
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
