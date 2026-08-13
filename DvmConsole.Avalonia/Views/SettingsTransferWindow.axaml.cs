// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Presentation-only settings transfer shell. The view-model owns category
    /// selection and the transfer service owns file mutation; this window owns
    /// native picker and confirmation adapters plus user-visible status.
    /// </summary>
    internal partial class SettingsTransferWindow : Window
    {
        private readonly IFileDialogService fileDialogService;
        private readonly IConfirmationService confirmationService;
        private readonly Func<Task> reloadRuntimeAsync;

        public SettingsTransferWindow(
            SettingsTransferViewModel viewModel,
            IFileDialogService fileDialogService,
            IConfirmationService confirmationService,
            Func<Task> reloadRuntimeAsync)
        {
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            this.fileDialogService = fileDialogService
                ?? throw new ArgumentNullException(nameof(fileDialogService));
            this.confirmationService = confirmationService
                ?? throw new ArgumentNullException(nameof(confirmationService));
            this.reloadRuntimeAsync = reloadRuntimeAsync
                ?? throw new ArgumentNullException(nameof(reloadRuntimeAsync));

            InitializeComponent();
        }

        private SettingsTransferViewModel? ViewModel => DataContext as SettingsTransferViewModel;

        private void SelectAll_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel?.SelectAll();
            ClearStatus();
        }

        private void SelectNone_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel?.SelectNone();
            ClearStatus();
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel)
                return;

            try
            {
                FileDialogResult result = await fileDialogService.SaveFileAsync(
                    new SaveFileRequest(
                        "Export Settings",
                        new[]
                        {
                            new DvmConsole.Platform.Dialogs.FileDialogFilter("JSON settings", new[] { "*.json" }),
                        },
                        "dvmconsole-settings.json",
                        null),
                    CancellationToken.None);
                if (result.Cancelled || string.IsNullOrWhiteSpace(result.Selected))
                    return;

                bool succeeded = await viewModel.ExportAsync(
                    result.Selected,
                    CancellationToken.None);
                SetStatus(succeeded
                    ? "Settings exported."
                    : "Unable to export settings.",
                    succeeded);
            }
            catch (OperationCanceledException)
            {
                // The dialog contract normally returns cancellation; retain a
                // defensive boundary for providers that throw it.
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to export settings: {exception.Message}", false);
            }
        }

        private async void Import_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel)
                return;

            try
            {
                FileDialogResult result = await fileDialogService.OpenFileAsync(
                    new OpenFileRequest(
                        "Import Settings",
                        new[]
                        {
                            new DvmConsole.Platform.Dialogs.FileDialogFilter("JSON settings", new[] { "*.json" }),
                        },
                        false,
                        null),
                    CancellationToken.None);
                if (result.Cancelled || string.IsNullOrWhiteSpace(result.Selected))
                    return;

                bool succeeded = await viewModel.ImportAsync(
                    result.Selected,
                    () => confirmationService.ConfirmAsync(
                        this,
                        new ConfirmationRequest(
                            "Import Settings",
                            "Importing settings will overwrite the selected categories in this console profile. Continue?"),
                        CancellationToken.None),
                    reloadRuntimeAsync,
                    CancellationToken.None);
                SetStatus(succeeded
                    ? "Settings imported and runtime refreshed."
                    : "Settings import cancelled or failed.",
                    succeeded);
            }
            catch (OperationCanceledException)
            {
                // See Export_Click: cancellation is a normal dialog outcome.
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to import settings: {exception.Message}", false);
            }
        }

        private async void Reset_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel)
                return;

            try
            {
                bool succeeded = await viewModel.ResetAsync(
                    () => confirmationService.ConfirmAsync(
                        this,
                        new ConfirmationRequest(
                            "Reset Settings",
                            "Reset all saved console settings and restart with defaults?"),
                        CancellationToken.None),
                    reloadRuntimeAsync,
                    CancellationToken.None);

                SetStatus(succeeded
                    ? "Settings reset and runtime refreshed."
                    : "Settings reset cancelled.",
                    succeeded);
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to reset settings: {exception.Message}", false);
            }
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetStatus(string message, bool success)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Foreground = success
                ? global::Avalonia.Media.Brushes.LightGreen
                : global::Avalonia.Media.Brushes.OrangeRed;
        }

        private void ClearStatus()
        {
            StatusTextBlock.Text = string.Empty;
        }
    }
}
