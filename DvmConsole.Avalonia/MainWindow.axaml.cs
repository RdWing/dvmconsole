// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
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
using DvmConsole.Core.Networking;
using DvmConsole.Platform.Audio;
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
        /// Resolves the primary channel's codeplug channel name onto the
        /// router's <see cref="TransmitTarget"/>; null until a codeplug
        /// is supplied to the full-arity constructor, keeping the PTT
        /// path a documented no-op.
        /// </summary>
        private readonly TransmitTargetResolver? transmitTargetResolver = null;

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
        /// slice, and the optional codeplug backing the transmit-target
        /// resolver (temporary channel assignment until the zone UI
        /// slice). The
        /// factory, readiness, systems, codec, sender, transport and
        /// codeplug parameters are
        /// last so the pre-existing four-argument constructor remains
        /// source-compatible, including null-literal calls. When a
        /// key-state reader is present, exactly one 250 ms dispatcher
        /// timer polls the PTT hotkey and detaches on close. When audio
        /// settings are present, this window subscribes once to their
        /// property changes and applies selections to the ComboBoxes.
        /// When an audio stream factory is supplied, a talkgroup audio
        /// router is composed over it (owning the shared factory) with
        /// the injected codec and traffic seams — the null codec pair and
        /// stub sender when none are supplied, until the real adapters
        /// land — and disposed on window close after the FNE slice;
        /// otherwise the audio slice stays dormant. The receive glue is
        /// composed with the router and subscribes to every adapter the
        /// transport factory creates, routing received voice frames into
        /// the router. A null or empty
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
            Codeplug? codeplug = null)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(systems, catalog, hotkeys, persistence, vocoderStatus, codeplug);

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
            }

            // Compose the talkgroup audio router over the shared factory
            // when one was supplied; the router owns the factory and is
            // disposed on window close. The null codec pair and the stub
            // traffic sender are the placeholder seams until the
            // Platform-native vocoder adapter and the fnecore traffic
            // adapter land (follow-on slices): the router stays fully
            // wired while decoding/encoding is inert and no traffic is
            // sent. Null keeps the audio slice dormant.
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
                    () => audioViewModel.AudioSettings?.SelectedOutputId ?? AudioDeviceId.Default);

                // Wire the FNE receive path into the router: the glue
                // classifies adapter frame events and routes voice
                // frames by talkgroup key. The factory's creation hook
                // subscribes the glue to every adapter as the
                // connection service creates it — including the fresh
                // adapters a Restart creates — so no service or bridge
                // change is needed.
                fneReceiveGlue = new FneReceiveGlue(
                    (key, frame, mode) => talkgroupAudioRouter!.RouteVoiceFrame(key, frame, mode));

                if (fnecoreTransportFactory is { } factory)
                {
                    factory.OnCreated += adapter =>
                    {
                        adapter.DmrFrameReceived += e => fneReceiveGlue?.OnDmrFrame(adapter.ConfiguredSystemName, e);
                        adapter.P25FrameReceived += e => fneReceiveGlue?.OnP25Frame(adapter.ConfiguredSystemName, e);
                    };
                }

                if (audioViewModel.Ptt is { } ptt)
                {
                    ptt.PttStateRequested += OnPttStateRequested;
                }

                talkgroupAudioRouter.CaptureEnded += OnCaptureEnded;
                talkgroupAudioRouter.MonitorStreamEnded += OnMonitorStreamEnded;
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
            }
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
            if (watchdogTimer is { } timer)
            {
                timer.Tick -= OnWatchdogTick;
                timer.Stop();
            }

            fneConnectionBridge?.Dispose();
            fneConnectionService?.Dispose();
            fneReceiveGlue?.Dispose();

            if (talkgroupAudioRouter is { } router)
            {
                _ = router.DisposeAsync();
            }
        }

        /// <summary>
        /// Routes a PTT engagement change from the view-model's PTT slice
        /// to the talkgroup audio router: engage begins a transmit for
        /// the resolved target, release ends it. A press with no resolved
        /// target (channel assignment has not landed) is a no-op. The
        /// event may arrive on the UI thread only (the PTT slice is
        /// UI-thread driven); the router itself is thread-safe either
        /// way.
        /// </summary>
        private void OnPttStateRequested(bool isDown)
        {
            if (talkgroupAudioRouter is not { } router)
            {
                return;
            }

            if (isDown)
            {
                var target = ResolveTransmitTarget();
                if (target is not { } resolved)
                {
                    return;
                }

                var inputDeviceId = (DataContext as MainWindowViewModel)?.AudioSettings?.SelectedInputId
                    ?? AudioDeviceId.Default;
                _ = router.BeginTransmitAsync(resolved, inputDeviceId, CancellationToken.None);
            }
            else
            {
                _ = router.EndTransmitAsync();
            }
        }

        /// <summary>
        /// Resolves the transmit target for a PTT press from the primary
        /// channel's codeplug channel name via the composed
        /// <see cref="TransmitTargetResolver"/>. Returns null — making
        /// the PTT press a no-op, the audio router untouched — when no
        /// resolver was composed (no codeplug), the view-model is
        /// absent, no primary channel is set, or the primary channel's
        /// name does not resolve to a transmittable target.
        /// </summary>
        private TransmitTarget? ResolveTransmitTarget()
            => transmitTargetResolver is { } resolver
                && DataContext is MainWindowViewModel vm
                && vm.PrimaryChannel?.ChannelName is { } name
                    ? resolver.Resolve(name)
                    : null;

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
