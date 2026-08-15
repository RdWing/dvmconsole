using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

public sealed partial class OperatorToolsWindow : Window
{
    private readonly MainWindowViewModel viewModel;

    public OperatorToolsWindow()
    {
        viewModel = null!;
        InitializeComponent();
    }

    public OperatorToolsWindow(MainWindowViewModel viewModel, OperatorToolSection section)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        ToolTabs.SelectedIndex = (int)section;
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private async void HandleTestPermitToneClick(object? sender, RoutedEventArgs e)
        => await viewModel.TestTalkPermitToneAsync();

    private void HandleSaveAudioInputPresetClick(object? sender, RoutedEventArgs e)
        => viewModel.SaveAudioInputPreset();

    private void HandleUseAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset })
            viewModel.UseAudioInputPreset(preset);
    }

    private void HandleDeleteAudioInputPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioInputPresetViewModel preset })
            viewModel.DeleteAudioInputPreset(preset);
    }

    private void HandleUseDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset })
            viewModel.UseDtmfPreset(preset);
    }

    private async void HandleSendDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset })
            await viewModel.SendDtmfPresetAsync(preset);
    }

    private void HandleDeleteDtmfPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DtmfPresetViewModel preset })
            viewModel.DeleteDtmfPreset(preset);
    }

    private void HandleUseTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset })
            viewModel.UseTonePreset(preset);
    }

    private async void HandleSendTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset })
            await viewModel.SendTonePresetAsync(preset);
    }

    private async void HandleSendQuickCallClick(object? sender, RoutedEventArgs e)
        => await viewModel.SendQuickCallAsync();

    private void HandleSaveToolbarClocksClick(object? sender, RoutedEventArgs e)
        => viewModel.SaveToolbarClocks();

    private void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
        => viewModel.ResetLayout();

    private async void HandleImportAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import alert audio",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("WAV or MPEG audio")
                {
                    Patterns = ["*.wav", "*.mp3", "*.mpeg", "*.mp2"],
                    MimeTypes = ["audio/wav", "audio/x-wav", "audio/mpeg"],
                    AppleUniformTypeIdentifiers = ["com.microsoft.waveform-audio", "public.mp3", "public.mpeg-4-audio"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.AddAlertTone(path);
    }

    private async void HandleSendAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AlertToneViewModel tone })
            await viewModel.SendAlertToneAsync(tone);
    }

    private void HandleDeleteAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AlertToneViewModel tone })
            viewModel.DeleteAlertTone(tone);
    }

    private void HandleDeleteTonePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TonePresetViewModel preset })
            viewModel.DeleteTonePreset(preset);
    }

    private void HandleSaveWebStreamOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WebStreamViewModel stream })
            viewModel.SaveWebStreamOutputDevice(stream);
    }

    private void HandleSaveOutputDeviceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel })
            viewModel.SaveChannelOutputDevice(channel);
    }

    private void HandleSaveIgnoredSubscribersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChannelViewModel channel })
            viewModel.TrySaveRecordingIgnoredSubscribers(channel);
    }

    private void HandleClearRecordingFiltersClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearRecordingFilters();

    private void HandleResetRecordingColumnsClick(object? sender, RoutedEventArgs e)
        => viewModel.ResetRecordingColumns();

    private void HandleApplyRecordingRootClick(object? sender, RoutedEventArgs e)
        => viewModel.ApplyRecordingRoot();

    private async void HandleChooseRecordingRootClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose recording folder",
                AllowMultiple = false
            });
        if (folders.Count == 0)
            return;

        string? path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        viewModel.RecordingRootPathText = path;
        viewModel.ApplyRecordingRoot();
    }

    private void HandleOpenRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata })
            viewModel.OpenRecording(metadata);
    }

    private async void HandlePlayRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata })
            await viewModel.PlayRecordingAsync(metadata);
    }

    private async void HandleStopRecordingClick(object? sender, RoutedEventArgs e)
        => await viewModel.StopRecordingPlaybackAsync();

    private async void HandleDeleteRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata })
            await viewModel.DeleteRecordingAsync(metadata);
    }

    private void HandleApplyPatchGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group })
            viewModel.ApplyPatchGroup(group);
    }

    private async void HandleMultiSelectPttClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group })
            await viewModel.ToggleMultiSelectPttAsync(group);
    }

    private async void HandleExportCallHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
            return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Call History",
            SuggestedFileName = "dvmconsole-call-history.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"],
                    AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"]
                }
            ]
        });
        string? path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.ExportCallHistory(path);
    }

    private void HandleClearCallHistoryClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearCallHistory();

    private async void HandleConnectSystemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await system.StartAsync();
    }

    private async void HandleDisconnectSystemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await system.StopAsync();
    }

    private void HandleCloseClick(object? sender, RoutedEventArgs e) => Close();
}

public enum OperatorToolSection
{
    Audio,
    Tones,
    Streams,
    Recorder,
    History,
    Groups,
    Connections,
    General
}
