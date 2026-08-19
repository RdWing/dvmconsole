using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Audio;
using DvmConsole.Media;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

public sealed partial class OperatorToolsWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DispatcherTimer scrollBarHideTimer;
    private ScrollViewer? activeScrollViewer;
    private ListBox? historyList;
    private INotifyCollectionChanged? historyCollection;
    private CallHistoryEntry? pendingHistoryAnchor;
    private double pendingHistoryAnchorY;
    private double pendingHistoryExtentHeight;
    private bool restoringHistoryViewport;

    internal bool IsHistoryViewportHookAttached => historyList is not null;

    public OperatorToolsWindow()
    {
        viewModel = null!;
        scrollBarHideTimer = CreateScrollBarHideTimer();
        InitializeComponent();
    }

    public OperatorToolsWindow(MainWindowViewModel viewModel, OperatorToolSection section)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        scrollBarHideTimer = CreateScrollBarHideTimer();
        InitializeComponent();
        TabControl tabs = ToolTabs ?? this.FindControl<TabControl>("ToolTabs")
            ?? throw new InvalidOperationException("The operator tools tab control could not be loaded.");
        ToolTabs = tabs;
        DataContext = viewModel;
        SelectSection(section);
        AddHandler(InputElement.KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerWheelChangedEvent, HandlePointerWheelChanged, RoutingStrategies.Tunnel);
        historyCollection = viewModel.FilteredCallHistory;
        historyCollection.CollectionChanged += HandleHistoryCollectionChanged;
        Opened += HandleOpened;
        ToolTabs.SelectionChanged += HandleToolTabsSelectionChanged;
        Closed += HandleClosed;
        ScheduleHistoryViewportHook();
    }

    public void SelectSection(OperatorToolSection section)
    {
        if (section == OperatorToolSection.Clock)
        {
            ToolTabs.SelectedIndex = (int)OperatorToolSection.General;
            Dispatcher.UIThread.Post(
                () => this.FindControl<TextBlock>("ClockSettingsSection")?.BringIntoView(),
                DispatcherPriority.Background);
            return;
        }

        ToolTabs.SelectedIndex = (int)section;
    }

    private DispatcherTimer CreateScrollBarHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (activeScrollViewer is not null)
                activeScrollViewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
            activeScrollViewer = null;
        };
        return timer;
    }

    private void HandlePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ScrollViewer? scrollViewer = (e.Source as Visual)?.GetVisualAncestors()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
            return;

        if (activeScrollViewer is not null && !ReferenceEquals(activeScrollViewer, scrollViewer))
            activeScrollViewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        activeScrollViewer = scrollViewer;
        scrollViewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        scrollBarHideTimer.Stop();
        scrollBarHideTimer.Start();
    }

    private void HandleOpened(object? sender, EventArgs e)
        => ScheduleHistoryViewportHook();

    private void HandleToolTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ScheduleHistoryViewportHook();

    private void ScheduleHistoryViewportHook()
        => Dispatcher.UIThread.Post(TryAttachHistoryViewportHook, DispatcherPriority.Background);

    private void TryAttachHistoryViewportHook()
    {
        if (historyList is not null)
            return;

        ListBox? list = HistoryList ??
            this.FindControl<ListBox>("HistoryList") ??
            this.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(candidate => candidate.Name == "HistoryList");
        if (list is null)
            return;

        historyList = list;
        historyList.LayoutUpdated += HandleHistoryListLayoutUpdated;
    }

    private void HandleHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (pendingHistoryAnchor is not null || restoringHistoryViewport)
            return;

        ScrollViewer? scrollViewer = GetHistoryScrollViewer();
        ListBox? list = historyList;
        if (list is null || scrollViewer is null || scrollViewer.Offset.Y <= 0.5)
            return;

        var visibleItems = list.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => new
            {
                Item = item,
                Entry = item.DataContext as CallHistoryEntry ?? item.Content as CallHistoryEntry,
                Position = item.TranslatePoint(default, scrollViewer)
            })
            .Where(candidate =>
                candidate.Entry is not null &&
                candidate.Position is Point position &&
                position.Y + candidate.Item.Bounds.Height > 0 &&
                position.Y < scrollViewer.Viewport.Height)
            .OrderBy(candidate => candidate.Position!.Value.Y)
            .FirstOrDefault();

        if (visibleItems?.Entry is null || visibleItems.Position is not Point anchorPosition)
            return;

        pendingHistoryAnchor = visibleItems.Entry;
        pendingHistoryAnchorY = anchorPosition.Y;
        pendingHistoryExtentHeight = scrollViewer.Extent.Height;
    }

    private void HandleHistoryListLayoutUpdated(object? sender, EventArgs e)
    {
        if (pendingHistoryAnchor is not CallHistoryEntry anchor)
            return;

        ScrollViewer? scrollViewer = GetHistoryScrollViewer();
        ListBox? list = historyList;
        if (list is null || scrollViewer is null)
            return;

        ListBoxItem? anchorItem = list.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(item =>
                ReferenceEquals(item.DataContext, anchor) ||
                ReferenceEquals(item.Content, anchor));
        Point? anchorPosition = anchorItem?.TranslatePoint(default, scrollViewer);
        double itemDelta = anchorPosition is Point position
            ? position.Y - pendingHistoryAnchorY
            : scrollViewer.Extent.Height - pendingHistoryExtentHeight;

        pendingHistoryAnchor = null;
        if (Math.Abs(itemDelta) <= 0.25)
            return;

        double desiredOffset = CalculateAnchoredHistoryOffset(
            scrollViewer.Offset.Y,
            itemDelta,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height);
        restoringHistoryViewport = true;
        try
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, desiredOffset);
        }
        finally
        {
            restoringHistoryViewport = false;
        }
    }

    private ScrollViewer? GetHistoryScrollViewer()
        => historyList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    internal static double CalculateAnchoredHistoryOffset(
        double currentOffset,
        double itemDelta,
        double extentHeight,
        double viewportHeight)
    {
        double maximumOffset = Math.Max(0, extentHeight - viewportHeight);
        return Math.Clamp(currentOffset + itemDelta, 0, maximumOffset);
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        scrollBarHideTimer.Stop();
        Opened -= HandleOpened;
        ToolTabs.SelectionChanged -= HandleToolTabsSelectionChanged;
        if (historyList is not null)
            historyList.LayoutUpdated -= HandleHistoryListLayoutUpdated;
        historyList = null;
        if (historyCollection is not null)
            historyCollection.CollectionChanged -= HandleHistoryCollectionChanged;
        historyCollection = null;
        pendingHistoryAnchor = null;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (MainWindow.TryMapPttKey(e.Key, out KeyboardPttKey key))
        {
            bool handled = viewModel.HandleKeyboardPttDown(key);
            e.Handled = handled || viewModel.IsConfiguredPttKey(key);
        }
    }

    private void HandleKeyUp(object? sender, KeyEventArgs e)
    {
        if (MainWindow.TryMapPttKey(e.Key, out KeyboardPttKey key))
        {
            bool handled = viewModel.HandleKeyboardPttUp(key);
            e.Handled = handled || viewModel.IsConfiguredPttKey(key);
        }
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private async void HandleTestPermitToneClick(object? sender, RoutedEventArgs e)
        => await viewModel.TestTalkPermitToneAsync();

    private void HandleRequestMacOsMicrophonePermissionClick(object? sender, RoutedEventArgs e)
        => viewModel.RequestMacOsMicrophonePermission();

    private void HandleRequestMacOsKeyboardPermissionClick(object? sender, RoutedEventArgs e)
        => viewModel.RequestMacOsKeyboardPermission();

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

    private void HandleAddToneStepClick(object? sender, RoutedEventArgs e)
        => viewModel.AddToneSequenceStep(silence: false);

    private void HandleAddSilenceStepClick(object? sender, RoutedEventArgs e)
        => viewModel.AddToneSequenceStep(silence: true);

    private void HandleRemoveToneStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToneSequenceStepViewModel step })
            viewModel.RemoveToneSequenceStep(step);
    }

    private void HandleMoveToneStepUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToneSequenceStepViewModel step })
            viewModel.MoveToneSequenceStep(step, -1);
    }

    private void HandleMoveToneStepDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToneSequenceStepViewModel step })
            viewModel.MoveToneSequenceStep(step, 1);
    }

    private void HandleSaveToolbarClocksClick(object? sender, RoutedEventArgs e)
        => viewModel.SaveToolbarClocks();

    private void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
        => viewModel.ResetLayout();

    private void HandleRefreshSerialPttDevicesClick(object? sender, RoutedEventArgs e)
        => viewModel.RefreshSerialPttDevices();

    private async void HandleApplySerialPttSettingsClick(object? sender, RoutedEventArgs e)
        => await viewModel.ApplySerialPttSettingsAsync();

    private async void HandleApplyGlobalPttKeyClick(object? sender, RoutedEventArgs e)
        => await viewModel.ApplyGlobalPttKeySelectionAsync();

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
                new FilePickerFileType("WAV, MPEG, or Ogg Opus audio")
                {
                    Patterns = ["*.wav", "*.mp3", "*.mpeg", "*.mp2", "*.ogg", "*.opus"],
                    MimeTypes = ["audio/wav", "audio/x-wav", "audio/mpeg", "audio/ogg", "audio/opus"],
                    AppleUniformTypeIdentifiers = ["com.microsoft.waveform-audio", "public.mp3", "public.mpeg-4-audio", "org.xiph.ogg"]
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

    private void HandleClearHistoryFiltersClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearHistoryFilters();

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

    private async void HandlePlayCallHistoryRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallHistoryEntry entry })
            await viewModel.PlayCallHistoryRecordingAsync(entry);
    }

    private async void HandleDeleteRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CallRecordingMetadata metadata } &&
            await ConfirmAsync(
                "Delete recording",
                $"Delete '{metadata.FileName}' and its catalog metadata? This cannot be undone.",
                "Delete"))
            await viewModel.DeleteRecordingAsync(metadata);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) =>
        {
            confirmed = true;
            parts.Window.Close();
        };
        await parts.Window.ShowDialog(this);
        return confirmed;
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

    private async void HandleRestartSystemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await system.RestartAsync();
    }

    private void HandleCloseClick(object? sender, RoutedEventArgs e) => Close();
}

public enum OperatorToolSection
{
    General,
    Audio,
    Tones,
    Streams,
    Recorder,
    History,
    Groups,
    Connections,
    Ptt,
    Clock
}
