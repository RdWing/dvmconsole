// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using dvmconsole;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// TAR recording viewer shell over the headless
    /// <see cref="TarViewerViewModel"/>. Playback, file-manager reveal, and
    /// destructive confirmation are injected; this window owns only UI events,
    /// selection, status text, and playback cancellation.
    /// </summary>
    internal partial class TarViewerWindow : Window, INotifyPropertyChanged
    {
        private readonly TarViewerViewModel viewModel;
        private readonly IAudioWaveFilePlayer waveFilePlayer;
        private readonly IFileRevealService fileRevealService;
        private readonly IConfirmationService confirmationService;
        private CancellationTokenSource? playbackCancellation;
        private string? currentPlaybackPath;
        private TarViewerViewModel.TarRecordingListItem? selectedItem;
        private string statusText = string.Empty;

        public TarViewerWindow(
            TarViewerViewModel viewModel,
            IAudioWaveFilePlayer waveFilePlayer,
            IFileRevealService fileRevealService,
            IConfirmationService confirmationService)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            this.waveFilePlayer = waveFilePlayer ?? throw new ArgumentNullException(nameof(waveFilePlayer));
            this.fileRevealService = fileRevealService ?? throw new ArgumentNullException(nameof(fileRevealService));
            this.confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));

            InitializeComponent();
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.Rows.CollectionChanged += Rows_CollectionChanged;
            InitializeColumnVisibilityMenu();
            RefreshView(rebuildIndex: false);
        }

        private void ColumnsButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu is { } menu)
            {
                menu.PlacementTarget = button;
                menu.Open(button);
            }
        }

        private void InitializeColumnVisibilityMenu()
        {
            foreach (MenuItem item in new[]
            {
                TimeColumnMenuItem,
                DurationColumnMenuItem,
                ChannelColumnMenuItem,
                TalkgroupColumnMenuItem,
                SourceIdColumnMenuItem,
                AliasColumnMenuItem,
                DirectionColumnMenuItem,
                ProtocolColumnMenuItem,
                SystemColumnMenuItem,
                EncryptionColumnMenuItem
            })
            {
                if (item.Tag is string key
                    && viewModel.Columns.FirstOrDefault(column =>
                        string.Equals(column.Key, key, StringComparison.OrdinalIgnoreCase)) is { } column)
                {
                    item.IsChecked = column.IsVisible;
                }
            }
        }

        private void ColumnVisibilityMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string key)
                return;

            if (!viewModel.ColumnVisibility.TrySetVisibility(key, item.IsChecked))
                return;

            try
            {
                viewModel.ColumnVisibility.Save();
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to save TAR Viewer columns: {exception.Message}");
            }
        }

        public string StatusText
        {
            get => statusText;
            private set
            {
                if (statusText == value)
                    return;
                statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        private void RecordingsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            selectedItem = RecordingsList.SelectedItem as TarViewerViewModel.TarRecordingListItem;
        }

        private void Refresh_Click(object? sender, RoutedEventArgs e)
        {
            RefreshView(rebuildIndex: true);
        }

        private async void Play_Click(object? sender, RoutedEventArgs e)
        {
            TarRecordingMetadata? metadata = selectedItem?.Metadata;
            if (metadata is null)
                return;
            if (string.IsNullOrWhiteSpace(metadata.FilePath) || !File.Exists(metadata.FilePath))
            {
                SetStatus("The selected TAR recording file is missing.");
                RefreshView(rebuildIndex: false);
                return;
            }

            await StopPlaybackAsync();
            var cancellation = new CancellationTokenSource();
            playbackCancellation = cancellation;
            currentPlaybackPath = metadata.FilePath;
            try
            {
                SetStatus($"Playing {metadata.FileName}.");
                AudioPlaybackResult result = await waveFilePlayer.PlayWavAsync(
                    metadata.FilePath,
                    cancellation.Token);
                if (result.Outcome == AudioPlaybackOutcome.Failed)
                    SetStatus($"Unable to play TAR recording: {result.ErrorMessage ?? "unknown error"}");
                else if (result.Outcome == AudioPlaybackOutcome.Completed)
                    SetStatus("Playback complete.");
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to play TAR recording: {exception.Message}");
            }
            finally
            {
                if (ReferenceEquals(playbackCancellation, cancellation))
                {
                    playbackCancellation = null;
                    currentPlaybackPath = null;
                }
                cancellation.Dispose();
            }
        }

        private async void Stop_Click(object? sender, RoutedEventArgs e)
        {
            await StopPlaybackAsync();
            SetStatus("Playback stopped.");
        }

        private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            string? filePath = selectedItem?.Metadata?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                SetStatus("The selected TAR recording file is missing.");
                return;
            }

            try
            {
                bool revealed = await fileRevealService.RevealAsync(filePath, CancellationToken.None);
                SetStatus(revealed ? "Recording revealed in the file manager." : "Unable to reveal recording.");
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to reveal recording: {exception.Message}");
            }
        }

        private async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            TarRecordingMetadata? metadata = selectedItem?.Metadata;
            if (metadata is null)
                return;

            try
            {
                bool confirmed = await confirmationService.ConfirmAsync(
                    this,
                    new ConfirmationRequest(
                        "Delete TAR recording",
                        $"Delete TAR recording '{metadata.FileName}'?"),
                    CancellationToken.None);
                if (!confirmed)
                    return;

                if (string.Equals(currentPlaybackPath, metadata.FilePath, StringComparison.OrdinalIgnoreCase))
                    await StopPlaybackAsync();
                viewModel.Recorder.DeleteRecording(metadata);
                RefreshView(rebuildIndex: false);
                SetStatus("Recording deleted.");
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to delete recording: {exception.Message}");
            }
        }

        private void ClearFilters_Click(object? sender, RoutedEventArgs e)
        {
            viewModel.ClearFilters();
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RefreshView(bool rebuildIndex)
        {
            try
            {
                viewModel.Refresh(rebuildIndex);
                if (viewModel.Rows.Count == 0)
                    SetStatus("No TAR recordings match the current view.");
                else
                    SetStatus(string.Empty);
            }
            catch (Exception exception)
            {
                SetStatus($"Unable to index TAR recordings: {exception.Message}");
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TarViewerViewModel.SearchText)
                or nameof(TarViewerViewModel.SelectedDirectionFilter)
                or nameof(TarViewerViewModel.SelectedProtocolFilter)
                or nameof(TarViewerViewModel.SelectedEncryptionFilter)
                or nameof(TarViewerViewModel.SystemFilter)
                or nameof(TarViewerViewModel.ChannelFilter)
                or nameof(TarViewerViewModel.TalkgroupFilter)
                or nameof(TarViewerViewModel.SourceIdFilter)
                or nameof(TarViewerViewModel.AliasFilter)
                or nameof(TarViewerViewModel.StartDateFilter)
                or nameof(TarViewerViewModel.EndDateFilter))
            {
                UpdateStatusFromRows();
            }
        }

        private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateStatusFromRows();
        }

        private void UpdateStatusFromRows()
        {
            string message = viewModel.Rows.Count == 0
                ? "No TAR recordings match the current view."
                : string.Empty;
            if (StatusText != message)
                SetStatus(message);
        }

        private async Task StopPlaybackAsync()
        {
            CancellationTokenSource? cancellation = playbackCancellation;
            cancellation?.Cancel();
            await waveFilePlayer.StopAsync();
            if (ReferenceEquals(playbackCancellation, cancellation))
            {
                playbackCancellation = null;
                currentPlaybackPath = null;
            }
        }

        private void SetStatus(string message)
        {
            StatusText = message;
            if (StatusTextBlock is not null)
                StatusTextBlock.Text = message;
        }

        protected override void OnClosed(EventArgs e)
        {
            playbackCancellation?.Cancel();
            _ = waveFilePlayer.StopAsync();
            playbackCancellation?.Dispose();
            playbackCancellation = null;
            base.OnClosed(e);
        }

        public new event PropertyChangedEventHandler? PropertyChanged;
    }
}
