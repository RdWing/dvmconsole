// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Dialogs;

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

        public AlertToneManagerWindow(
            AlertToneManagerViewModel viewModel,
            IFileDialogService fileDialogService,
            DvmConsole.Avalonia.Dialogs.IConfirmationService confirmationService)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            this.fileDialogService = fileDialogService
                ?? throw new ArgumentNullException(nameof(fileDialogService));
            this.confirmationService = confirmationService
                ?? throw new ArgumentNullException(nameof(confirmationService));

            InitializeComponent();
            DataContext = viewModel;
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

        private void Save_Click(object? sender, RoutedEventArgs e)
            => viewModel.Commit();

        private void Close_Click(object? sender, RoutedEventArgs e)
            => Close();
    }
}
