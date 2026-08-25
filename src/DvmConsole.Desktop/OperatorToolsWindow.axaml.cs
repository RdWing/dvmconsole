using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Media;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

public sealed partial class OperatorToolsWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly WindowPttKeyRouter pttKeyRouter;
    private readonly DispatcherTimer scrollBarHideTimer;
    private ScrollViewer? activeScrollViewer;
    private ListBox? historyList;
    private ScrollViewportAnchor<CallHistoryEntry>? historyViewportAnchor;
    private string? pendingSectionAnchorName;
    private bool synchronizingSectionNavigation;

    internal bool IsHistoryViewportHookAttached => historyList is not null;
    internal bool IsPendingSectionNavigation => pendingSectionAnchorName is not null;

    public OperatorToolsWindow()
    {
        viewModel = null!;
        pttKeyRouter = null!;
        scrollBarHideTimer = CreateScrollBarHideTimer();
        InitializeComponent();
        PopulateSectionNavigation();
    }

    public OperatorToolsWindow(MainWindowViewModel viewModel, OperatorToolSection section)
        : this(viewModel, section, new WindowPttKeyRouter(() => viewModel))
    {
    }

    internal OperatorToolsWindow(
        MainWindowViewModel viewModel,
        OperatorToolSection section,
        WindowPttKeyRouter pttKeyRouter)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.pttKeyRouter = pttKeyRouter ?? throw new ArgumentNullException(nameof(pttKeyRouter));
        scrollBarHideTimer = CreateScrollBarHideTimer();
        InitializeComponent();
        TabControl tabs = ToolTabs ?? this.FindControl<TabControl>("ToolTabs")
            ?? throw new InvalidOperationException("The operator tools tab control could not be loaded.");
        ToolTabs = tabs;
        SectionNavigation ??= this.FindControl<ListBox>("SectionNavigation")
            ?? throw new InvalidOperationException("The settings navigation list could not be loaded.");
        SectionSearchBox ??= this.FindControl<TextBox>("SectionSearchBox")
            ?? throw new InvalidOperationException("The settings search box could not be loaded.");
        NoSettingsSearchResults ??= this.FindControl<TextBlock>("NoSettingsSearchResults")
            ?? throw new InvalidOperationException("The settings search status could not be loaded.");
        PopulateSectionNavigation();
        DataContext = viewModel;
        SelectSection(section);
        AddHandler(InputElement.KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, HandleKeyUp, RoutingStrategies.Tunnel);
        AddHandler(InputElement.GotFocusEvent, HandlePttFocusChanged, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.LostFocusEvent, HandlePttFocusChanged, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerWheelChangedEvent, HandlePointerWheelChanged, RoutingStrategies.Tunnel);
        viewModel.FilteredCallHistoryChanging += HandleHistoryCollectionChanging;
        Opened += HandleOpened;
        LayoutUpdated += HandleWindowLayoutUpdated;
        ToolTabs.SelectionChanged += HandleToolTabsSelectionChanged;
        Closed += HandleClosed;
        Activated += (_, _) => UpdatePttFocusSuppression();
        Deactivated += (_, _) => pttKeyRouter.UpdateInputFocus(null);
        ScheduleHistoryViewportHook();
    }

    public void SelectSection(OperatorToolSection section)
    {
        pendingSectionAnchorName = null;
        synchronizingSectionNavigation = true;
        try
        {
            SelectNavigationItem(section);
            if (section == OperatorToolSection.Clock)
            {
                ToolTabs.SelectedIndex = (int)OperatorToolSection.General;
                Dispatcher.UIThread.Post(
                    () => this.FindControl<TextBlock>("ClockSettingsSection")?.BringIntoView(),
                    DispatcherPriority.Background);
                return;
            }

            if (section == OperatorToolSection.EncryptionKeys)
            {
                ToolTabs.SelectedIndex = (int)OperatorToolSection.Connections;
                pendingSectionAnchorName = "EncryptionKeyStatusSection";
                SchedulePendingSectionReveal();
                return;
            }

            ToolTabs.SelectedIndex = (int)section;
        }
        finally
        {
            synchronizingSectionNavigation = false;
        }
    }

    private void SelectNavigationItem(OperatorToolSection section)
    {
        ListBoxItem? item = SectionNavigation.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(candidate => TryGetNavigationSection(candidate, out OperatorToolSection candidateSection) &&
                                         candidateSection == section);
        if (item is not null)
            SectionNavigation.SelectedItem = item;
    }

    private static bool TryGetNavigationSection(ListBoxItem item, out OperatorToolSection section)
    {
        if (item.Tag is OperatorToolSectionDefinition definition)
        {
            section = definition.Section;
            return true;
        }
        section = default;
        return false;
    }

    private void PopulateSectionNavigation()
    {
        if (SectionNavigation is null || SectionNavigation.ItemsSource is not null)
            return;
        SectionNavigation.ItemsSource = OperatorToolSectionCatalog.All
            .Select(definition => new ListBoxItem
            {
                Content = definition.DisplayName,
                Tag = definition
            })
            .ToArray();
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
    {
        ScheduleHistoryViewportHook();
        SchedulePendingSectionReveal();
    }

    private void SchedulePendingSectionReveal()
    {
        if (pendingSectionAnchorName is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!TryRevealPendingSection())
                Dispatcher.UIThread.Post(() => TryRevealPendingSection(), DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private bool TryRevealPendingSection()
    {
        if (pendingSectionAnchorName is null)
            return true;

        Control? anchor = this.FindControl<Control>(pendingSectionAnchorName);
        ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("ConnectionsScrollViewer");
        if (anchor is null || scrollViewer is null || anchor.Bounds.Height <= 0)
            return false;

        Point? position = anchor.TranslatePoint(default, scrollViewer);
        if (position is null)
            return false;

        double maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        double desiredOffset = Math.Clamp(
            scrollViewer.Offset.Y + position.Value.Y - 8,
            0,
            maximumOffset);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, desiredOffset);
        pendingSectionAnchorName = null;
        return true;
    }

    private void HandleWindowLayoutUpdated(object? sender, EventArgs e)
        => TryAttachHistoryViewportHook();

    private void HandleToolTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleHistoryViewportHook();
        if (!synchronizingSectionNavigation &&
            ToolTabs.SelectedIndex >= 0 &&
            ToolTabs.SelectedIndex <= (int)OperatorToolSection.Ptt)
        {
            synchronizingSectionNavigation = true;
            try
            {
                SelectNavigationItem((OperatorToolSection)ToolTabs.SelectedIndex);
            }
            finally
            {
                synchronizingSectionNavigation = false;
            }
        }
    }

    private void HandleSectionNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingSectionNavigation ||
            SectionNavigation.SelectedItem is not ListBoxItem item ||
            !TryGetNavigationSection(item, out OperatorToolSection section))
        {
            return;
        }

        SelectSection(section);
    }

    private void HandleSectionSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        string query = SectionSearchBox.Text?.Trim() ?? string.Empty;
        int visibleCount = 0;
        foreach (ListBoxItem item in SectionNavigation.Items.OfType<ListBoxItem>())
        {
            string searchTerms = item.Tag is OperatorToolSectionDefinition definition
                ? definition.SearchTerms
                : string.Empty;
            string searchableText = $"{item.Content} {searchTerms}";
            item.IsVisible = query.Length == 0 ||
                             searchableText.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (item.IsVisible)
                visibleCount++;
        }

        NoSettingsSearchResults.IsVisible = visibleCount == 0;
    }

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
        LayoutUpdated -= HandleWindowLayoutUpdated;
        historyViewportAnchor = new ScrollViewportAnchor<CallHistoryEntry>(
            GetHistoryScrollViewer,
            () => list.GetVisualDescendants().OfType<ListBoxItem>(),
            control => control is ListBoxItem item
                ? item.DataContext as CallHistoryEntry ?? item.Content as CallHistoryEntry
                : null);
        historyList.LayoutUpdated += HandleHistoryListLayoutUpdated;
    }

    private void HandleHistoryCollectionChanging(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            historyViewportAnchor?.Reset();
        else
            historyViewportAnchor?.Capture();
    }

    private void HandleHistoryListLayoutUpdated(object? sender, EventArgs e)
        => historyViewportAnchor?.Restore();

    private ScrollViewer? GetHistoryScrollViewer()
        => historyList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void HandleClosed(object? sender, EventArgs e)
    {
        scrollBarHideTimer.Stop();
        Opened -= HandleOpened;
        LayoutUpdated -= HandleWindowLayoutUpdated;
        ToolTabs.SelectionChanged -= HandleToolTabsSelectionChanged;
        if (historyList is not null)
            historyList.LayoutUpdated -= HandleHistoryListLayoutUpdated;
        historyList = null;
        historyViewportAnchor?.Reset();
        historyViewportAnchor = null;
        viewModel.FilteredCallHistoryChanging -= HandleHistoryCollectionChanging;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F &&
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            SectionSearchBox.Focus();
            SectionSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (pttKeyRouter.TryHandleKeyDown(e.Key, out bool handled))
            e.Handled = handled;
    }

    private void HandleKeyUp(object? sender, KeyEventArgs e)
    {
        if (pttKeyRouter.TryHandleKeyUp(e.Key, out bool handled))
            e.Handled = handled;
    }

    private void HandlePttFocusChanged(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(UpdatePttFocusSuppression, DispatcherPriority.Input);

    private void UpdatePttFocusSuppression()
        => pttKeyRouter.UpdateInputFocus(FocusManager?.GetFocusedElement());

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

    private async void HandleApplyActiveSystemPttKeyClick(object? sender, RoutedEventArgs e)
        => await viewModel.ApplyActiveSystemPttKeySelectionAsync();

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

    private void HandleClearHistoryFiltersClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearHistoryFilters();

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

    private async void HandleToggleSystemConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await viewModel.ToggleSystemConnectionAsync(system);
    }

    private async void HandleRestartSystemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemViewModel system })
            await system.RestartAsync();
    }

    private void HandleCloseClick(object? sender, RoutedEventArgs e) => Close();
}
