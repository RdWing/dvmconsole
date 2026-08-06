// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia
{
    public partial class MainWindow : Window
    {
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
        /// When the audio slice is present, this window subscribes once
        /// to its property changes and applies the saved selections to
        /// the audio ComboBoxes. When a hotkey service is present, this
        /// window subscribes once to its
        /// <see cref="IGlobalHotkeyService.HotkeyPressed"/> event and
        /// marshals every event to the UI thread. No registration,
        /// unregistration, or disposal is performed here.
        /// </summary>
        public MainWindow(IAudioDeviceCatalog? catalog, IGlobalHotkeyService? hotkeys)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(null, catalog, hotkeys);

            if (hotkeys is not null)
            {
                hotkeys.HotkeyPressed += OnHotkeyPressed;
            }

            if (DataContext is MainWindowViewModel viewModel
                && viewModel.AudioSettings is { } settings)
            {
                settings.PropertyChanged += AudioSettings_PropertyChanged;
                ApplyAudioSelections();
            }
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
    }
}
