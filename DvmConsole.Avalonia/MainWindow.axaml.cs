// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        /// created in the five-dependency constructor. Systems stay null
        /// until a codeplug loader exists, so the slice is dormant: zero
        /// rows, no transports, no events.
        /// </summary>
        private IFneConnectionService? fneConnectionService = null;

        /// <summary>
        /// The bridge forwarding the FNE connection manager's requests
        /// into <see cref="fneConnectionService"/> and marshalling
        /// service state back onto the UI thread; null until composed.
        /// </summary>
        private FneConnectionServiceBridge? fneConnectionBridge = null;

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
        /// reader, optional audio-settings persistence, and the startup
        /// vocoder-readiness result. The readiness parameter is last so
        /// the pre-existing four-argument constructor remains
        /// source-compatible, including null-literal calls. When a
        /// key-state reader is present, exactly one 250 ms dispatcher
        /// timer polls the PTT hotkey and detaches on close. When audio
        /// settings are present, this window subscribes once to their
        /// property changes and applies selections to the ComboBoxes. No
        /// registration, unregistration, or disposal is performed here.
        /// </summary>
        public MainWindow(
            IAudioDeviceCatalog? catalog,
            IGlobalHotkeyService? hotkeys,
            IKeyboardKeyStateReader? keyStateReader,
            AudioSettingsPersistence? persistence,
            VocoderReadinessResult? vocoderStatus)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(null, catalog, hotkeys, persistence, vocoderStatus);

            // Compose the dormant headless FNE slice: systems stay null
            // until a codeplug loader exists, so no transport factory
            // call is ever made and no row can ever raise a request.
            // The bridge is inert with zero rows and safe to construct
            // in headless tests.
            if (DataContext is MainWindowViewModel viewModel)
            {
                fneConnectionService = new FneConnectionService(null, new UnavailableFneTransportFactory());
                fneConnectionBridge = new FneConnectionServiceBridge(fneConnectionService, viewModel.FneConnections);
                fneConnectionBridge.Attach();
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
        /// down the headless FNE slice when the window closes: the
        /// bridge detaches first (stopping event flow in both
        /// directions), then the service disconnects and disposes every
        /// transport and cancels all schedulers. Both disposals are
        /// idempotent, so a repeated close event is harmless.
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
