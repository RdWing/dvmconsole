// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
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
    /// Modeless owner-scoped viewer for the shared Core recent-log buffer.
    /// The view owns dispatcher, clipboard, and file-dialog interactions;
    /// the view-model remains headless.
    /// </summary>
    internal partial class DebugLogWindow : Window
    {
        private readonly IFileDialogService fileDialogService;
        private bool detached;

        public DebugLogWindow(
            DebugLogViewModel viewModel,
            IFileDialogService fileDialogService)
        {
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            this.fileDialogService = fileDialogService
                ?? throw new ArgumentNullException(nameof(fileDialogService));

            InitializeComponent();
            viewModel.Buffer.LogLineWritten += OnLogLineWritten;
            Closed += OnClosed;
        }

        private DebugLogViewModel? ViewModel => DataContext as DebugLogViewModel;

        private void OnLogLineWritten(string line)
        {
            if (detached)
                return;

            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!detached)
                    ViewModel?.AppendLine(line);
            });
        }

        private async void Copy_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel)
                return;

            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null)
                {
                    SetStatus("Clipboard is unavailable.", false);
                    return;
                }

                await clipboard.SetTextAsync(viewModel.GetTextSnapshot());
                SetStatus("Copied visible log lines.", true);
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to copy logs: {exception.Message}", false);
            }
        }

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel)
                return;

            try
            {
                FileDialogResult result = await fileDialogService.SaveFileAsync(
                    new SaveFileRequest(
                        "Save Debug Logs",
                        new[]
                        {
                            new DvmConsole.Platform.Dialogs.FileDialogFilter(
                                "Text files", new[] { "*.txt" }),
                        },
                        "dvmconsole-debug.log.txt",
                        null),
                    CancellationToken.None);
                if (result.Cancelled || string.IsNullOrWhiteSpace(result.Selected))
                    return;

                File.WriteAllText(result.Selected, viewModel.GetTextSnapshot());
                SetStatus("Saved visible log lines.", true);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a normal picker outcome.
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to save logs: {exception.Message}", false);
            }
        }

        private void Clear_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel?.Clear();
            SetStatus("Cleared the visible log view.", true);
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        private void OnClosed(object? sender, EventArgs e)
        {
            if (detached)
                return;

            detached = true;
            if (ViewModel is { } viewModel)
                viewModel.Buffer.LogLineWritten -= OnLogLineWritten;
            Closed -= OnClosed;
        }

        private void SetStatus(string message, bool success)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Foreground = success
                ? global::Avalonia.Media.Brushes.LightGreen
                : global::Avalonia.Media.Brushes.OrangeRed;
        }
    }
}
