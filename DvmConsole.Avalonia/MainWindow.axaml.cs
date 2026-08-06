// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia
{
    public partial class MainWindow : Window
    {
        public MainWindow()
            : this(null)
        {
        }

        /// <summary>
        /// Creates the dashboard window with the given audio device
        /// catalog composed into the view-model; a null catalog leaves
        /// the audio-settings slice absent.
        /// </summary>
        public MainWindow(IAudioDeviceCatalog? catalog)
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel(null, catalog);
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
    }
}
