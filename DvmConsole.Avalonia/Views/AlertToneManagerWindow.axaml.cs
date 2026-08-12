// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.ComponentModel;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Thin owner-bound shell for custom alert-tone management. The view model
    /// owns managed rows and save snapshots; this window owns picker and
    /// confirmation requests only.
    /// </summary>
    internal partial class AlertToneManagerWindow : Window
    {
        private readonly AlertToneManagerViewModel viewModel;
        private readonly IFileDialogService fileDialogService;
        private readonly DvmConsole.Avalonia.Dialogs.IConfirmationService confirmationService;
        private readonly AlertTonePreviewCoordinator? previewCoordinator;
        private string statusText = string.Empty;

        public AlertToneManagerWindow(
            AlertToneManagerViewModel viewModel,
            IFileDialogService fileDialogService,
            DvmConsole.Avalonia.Dialogs.IConfirmationService confirmationService,
            IAudioWaveFileInspector? waveFileInspector = null,
            IAudioWaveFilePlayer? waveFilePlayer = null)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            this.fileDialogService = fileDialogService
                ?? throw new ArgumentNullException(nameof(fileDialogService));
            this.confirmationService = confirmationService
                ?? throw new ArgumentNullException(nameof(confirmationService));
            if (waveFileInspector is not null && waveFilePlayer is not null)
            {
                previewCoordinator = new AlertTonePreviewCoordinator(
                    waveFileInspector,
                    waveFilePlayer);
            }

            InitializeComponent();
            DataContext = viewModel;
        }

        public string StatusText
        {
            get => statusText;
            private set
            {
                if (statusText == value)
                    return;
                statusText = value;
                if (StatusTextBlock is not null)
                    StatusTextBlock.Text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        private async void Add_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var result = await fileDialogService.OpenFileAsync(
                    new OpenFileRequest(
                        "Select Alert Tone(s)",
                        new[] { new DvmConsole.Platform.Dialogs.FileDialogFilter("WAV files", new[] { "*.wav" }) },
                        AllowMultiple: true,
                        InitialDirectory: null),
                    CancellationToken.None);
                if (!result.Cancelled)
                    viewModel.AddFiles(result.SelectedMany);
            }
            catch (OperationCanceledException)
            {
                // Picker cancellation is a no-op.
            }
        }

        private async void Browse_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.DataContext is not AlertToneManagerViewModel.AlertToneItem item)
            {
                return;
            }

            try
            {
                var result = await fileDialogService.OpenFileAsync(
                    new OpenFileRequest(
                        "Select Alert Tone",
                        new[] { new DvmConsole.Platform.Dialogs.FileDialogFilter("WAV files", new[] { "*.wav" }) },
                        AllowMultiple: false,
                        InitialDirectory: null),
                    CancellationToken.None);
                if (!result.Cancelled && result.Selected is { Length: > 0 } path)
                    viewModel.ReplaceFile(item, path);
            }
            catch (OperationCanceledException)
            {
                // Picker cancellation is a no-op.
            }
        }

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.DataContext is not AlertToneManagerViewModel.AlertToneItem item)
            {
                return;
            }

            bool confirmed = await confirmationService.ConfirmAsync(
                this,
                new DvmConsole.Avalonia.Dialogs.ConfirmationRequest(
                    "Delete Alert Tone",
                    $"Delete alert tone '{item.DisplayName}'?"),
                CancellationToken.None);
            if (confirmed)
                viewModel.Delete(item);
        }

        private async void Preview_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.DataContext is not AlertToneManagerViewModel.AlertToneItem item)
            {
                return;
            }

            if (previewCoordinator is null)
            {
                StatusText = "Preview is unavailable on this host.";
                return;
            }

            try
            {
                StatusText = $"Validating {item.DisplayName}...";
                AudioPlaybackResult result = await previewCoordinator.PreviewAsync(
                    item.FilePath,
                    CancellationToken.None);
                StatusText = result.Outcome switch
                {
                    AudioPlaybackOutcome.Completed => "Preview complete.",
                    AudioPlaybackOutcome.Cancelled => "Preview stopped.",
                    _ => $"Preview failed: {result.ErrorMessage ?? "unknown error"}",
                };
            }
            catch (Exception exception)
            {
                StatusText = $"Preview failed: {exception.Message}";
            }
        }

        private async void Stop_Click(object? sender, RoutedEventArgs e)
        {
            if (previewCoordinator is null)
                return;

            try
            {
                await previewCoordinator.StopAsync();
                StatusText = "Preview stopped.";
            }
            catch (Exception exception)
            {
                StatusText = $"Unable to stop preview: {exception.Message}";
            }
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
            => viewModel.Commit();

        private void Close_Click(object? sender, RoutedEventArgs e)
            => Close();

        protected override void OnClosed(EventArgs e)
        {
            _ = previewCoordinator?.DisposeAsync().AsTask();
            base.OnClosed(e);
        }

        public new event PropertyChangedEventHandler? PropertyChanged;
    }
}
