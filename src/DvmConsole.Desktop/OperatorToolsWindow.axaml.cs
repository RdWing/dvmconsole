using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Media;
using DvmConsole.Presentation;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

public sealed partial class OperatorToolsWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly WindowPttKeyRouter pttKeyRouter;
    private readonly DispatcherTimer scrollBarHideTimer;
    private ContentControl toolContent = null!;
    private GeneralSettingsView? generalSettingsView;
    private CallHistoryView? historyView;
    private DvmConsole.Presentation.ConnectionsSettingsView? connectionsSettingsView;
    private OperatorToolSection? mountedSection;
    private ScrollViewer? activeScrollViewer;
    private ListBox? historyList;
    private ScrollViewportAnchor<CallHistoryEntry>? historyViewportAnchor;
    private string? pendingSectionAnchorName;
    private bool synchronizingSectionNavigation;
    private bool closed;

    internal bool IsHistoryViewportHookAttached => historyList is not null;
    internal bool IsPendingSectionNavigation => pendingSectionAnchorName is not null;
    internal string PendingSectionNavigationDiagnostic
    {
        get
        {
            DvmConsole.Presentation.ConnectionsSettingsView? view = ResolveConnectionsSettingsView();
            ScrollViewer? scroller = view?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            TextBlock? anchor = view?.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(candidate => candidate.Text == "Channel key status");
            return $"section={mountedSection}; " +
                   $"view={view?.Bounds}; " +
                   $"scroller={scroller?.Bounds}; extent={scroller?.Extent}; viewport={scroller?.Viewport}; " +
                   $"anchor={anchor?.Bounds}; attached={anchor?.IsAttachedToVisualTree()}";
        }
    }

    public OperatorToolsWindow()
    {
        viewModel = null!;
        pttKeyRouter = null!;
        scrollBarHideTimer = CreateScrollBarHideTimer();
        InitializeComponent();
        ResolveShellControls();
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
        ResolveShellControls();
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
        Closed += HandleClosed;
        Activated += (_, _) => UpdatePttFocusSuppression();
        Deactivated += (_, _) => pttKeyRouter.UpdateInputFocus(null, isWindowActive: false);
        ScheduleHistoryViewportHook();
    }

    private void ResolveShellControls()
    {
        toolContent = this.FindControl<ContentControl>("ToolContent")
            ?? throw new InvalidOperationException("The operator tools content host could not be loaded.");
        SectionNavigation ??= this.FindControl<ListBox>("SectionNavigation")
            ?? throw new InvalidOperationException("The settings navigation list could not be loaded.");
        SectionSearchBox ??= this.FindControl<TextBox>("SectionSearchBox")
            ?? throw new InvalidOperationException("The settings search box could not be loaded.");
        NoSettingsSearchResults ??= this.FindControl<TextBlock>("NoSettingsSearchResults")
            ?? throw new InvalidOperationException("The settings search status could not be loaded.");
    }

    public void SelectSection(OperatorToolSection section)
    {
        if (closed)
            return;

        pendingSectionAnchorName = null;
        synchronizingSectionNavigation = true;
        try
        {
            SelectNavigationItem(section);
            if (section == OperatorToolSection.Clock)
            {
                MountSection(OperatorToolSection.General);
                Dispatcher.UIThread.Post(
                    TryRevealClockSettings,
                    DispatcherPriority.Background);
                return;
            }

            if (section == OperatorToolSection.EncryptionKeys)
            {
                MountSection(OperatorToolSection.Connections);
                pendingSectionAnchorName = "EncryptionKeyStatusSection";
                SchedulePendingSectionReveal();
                return;
            }

            MountSection(section);
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
        if (closed || pendingSectionAnchorName is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!TryRevealPendingSection() && !closed)
                Dispatcher.UIThread.Post(() => TryRevealPendingSection(), DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private bool TryRevealPendingSection()
    {
        if (closed)
        {
            pendingSectionAnchorName = null;
            return true;
        }
        if (pendingSectionAnchorName is null)
            return true;

        DvmConsole.Presentation.ConnectionsSettingsView? view = ResolveConnectionsSettingsView();
        if (view is null || !view.TryBringKeyStatusIntoView())
            return false;
        pendingSectionAnchorName = null;
        return true;
    }

    private void HandleWindowLayoutUpdated(object? sender, EventArgs e)
    {
        TryAttachHistoryViewportHook();
        TryRevealPendingSection();
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
    {
        if (!closed)
            Dispatcher.UIThread.Post(TryAttachHistoryViewportHook, DispatcherPriority.Background);
    }

    private void TryAttachHistoryViewportHook()
    {
        if (closed || historyList is not null)
            return;

        ListBox? list = historyView?.HistoryItems ??
            this.FindControl<ListBox>("HistoryList") ??
            this.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(candidate => candidate.Name == "HistoryList");
        if (list is null)
            return;

        historyList = list;
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
        closed = true;
        pendingSectionAnchorName = null;
        scrollBarHideTimer.Stop();
        Opened -= HandleOpened;
        LayoutUpdated -= HandleWindowLayoutUpdated;
        DetachHistoryViewport();
        viewModel.FilteredCallHistoryChanging -= HandleHistoryCollectionChanging;
    }

    private void DetachHistoryViewport()
    {
        if (historyList is not null)
            historyList.LayoutUpdated -= HandleHistoryListLayoutUpdated;
        historyList = null;
        historyViewportAnchor?.Reset();
        historyViewportAnchor = null;
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
    {
        if (!closed)
            Dispatcher.UIThread.Post(UpdatePttFocusSuppression, DispatcherPriority.Input);
    }

    private void UpdatePttFocusSuppression()
    {
        if (!closed)
            pttKeyRouter.UpdateInputFocus(FocusManager?.GetFocusedElement(), IsActive);
    }

    private void TryRevealClockSettings()
    {
        if (!closed)
            ResolveGeneralSettingsView()?.BringClockSettingsIntoView();
    }

    private GeneralSettingsView? ResolveGeneralSettingsView()
        => generalSettingsView ??= this.GetVisualDescendants()
            .OfType<GeneralSettingsView>()
            .FirstOrDefault();

    private DvmConsole.Presentation.ConnectionsSettingsView? ResolveConnectionsSettingsView()
        => connectionsSettingsView ??= this.GetVisualDescendants()
            .OfType<DvmConsole.Presentation.ConnectionsSettingsView>()
            .FirstOrDefault();

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private async void HandleTestPermitToneClick(object? sender, RoutedEventArgs e)
        => await viewModel.TestTalkPermitToneAsync();

    private async void HandleRequestMacOsMicrophonePermissionClick(object? sender, RoutedEventArgs e)
        => await viewModel.RequestMacOsMicrophonePermissionAsync();

    private async void HandleSharedTestPermitToneRequested(object? sender, EventArgs e)
        => await viewModel.TestTalkPermitToneAsync();

    private async void HandleSharedMicrophonePermissionRequested(object? sender, EventArgs e)
        => await viewModel.RequestMacOsMicrophonePermissionAsync();

    private void HandleSharedSaveAudioInputPresetRequested(object? sender, EventArgs e)
        => viewModel.SaveAudioInputPreset();

    private void HandleSharedUseAudioInputPresetRequested(object? sender, AudioInputPresetEventArgs e)
    {
        if (e.Preset is AudioInputPresetViewModel preset)
            viewModel.UseAudioInputPreset(preset);
    }

    private void HandleSharedDeleteAudioInputPresetRequested(object? sender, AudioInputPresetEventArgs e)
    {
        if (e.Preset is AudioInputPresetViewModel preset)
            viewModel.DeleteAudioInputPreset(preset);
    }

    private void HandleSharedKeyboardPermissionRequested(object? sender, EventArgs e)
        => viewModel.RequestMacOsKeyboardPermission();

    private void HandleSharedUseDtmfPresetRequested(object? sender, DtmfPresetEventArgs e)
    {
        if (e.Preset is DtmfPresetViewModel preset)
            viewModel.UseDtmfPreset(preset);
    }

    private async void HandleSharedSendDtmfPresetRequested(object? sender, DtmfPresetEventArgs e)
    {
        if (e.Preset is DtmfPresetViewModel preset)
            await viewModel.SendDtmfPresetAsync(preset);
    }

    private void HandleSharedDeleteDtmfPresetRequested(object? sender, DtmfPresetEventArgs e)
    {
        if (e.Preset is DtmfPresetViewModel preset)
            viewModel.DeleteDtmfPreset(preset);
    }

    private void HandleSharedUseTonePresetRequested(object? sender, TonePresetEventArgs e)
    {
        if (e.Preset is TonePresetViewModel preset)
            viewModel.UseTonePreset(preset);
    }

    private async void HandleSharedSendTonePresetRequested(object? sender, TonePresetEventArgs e)
    {
        if (e.Preset is TonePresetViewModel preset)
            await viewModel.SendTonePresetAsync(preset);
    }

    private void HandleSharedDeleteTonePresetRequested(object? sender, TonePresetEventArgs e)
    {
        if (e.Preset is TonePresetViewModel preset)
            viewModel.DeleteTonePreset(preset);
    }

    private async void HandleSharedSendQuickCallRequested(object? sender, EventArgs e)
        => await viewModel.SendQuickCallAsync();

    private void HandleSharedAddToneStepRequested(object? sender, EventArgs e)
        => viewModel.AddToneSequenceStep(silence: false);

    private void HandleSharedAddSilenceStepRequested(object? sender, EventArgs e)
        => viewModel.AddToneSequenceStep(silence: true);

    private void HandleSharedRemoveToneStepRequested(object? sender, ToneSequenceStepEventArgs e)
    {
        if (e.Step is ToneSequenceStepViewModel step)
            viewModel.RemoveToneSequenceStep(step);
    }

    private void HandleSharedMoveToneStepUpRequested(object? sender, ToneSequenceStepEventArgs e)
    {
        if (e.Step is ToneSequenceStepViewModel step)
            viewModel.MoveToneSequenceStep(step, -1);
    }

    private void HandleSharedMoveToneStepDownRequested(object? sender, ToneSequenceStepEventArgs e)
    {
        if (e.Step is ToneSequenceStepViewModel step)
            viewModel.MoveToneSequenceStep(step, 1);
    }

    private void HandleSharedSaveToolbarClocksRequested(object? sender, EventArgs e)
        => viewModel.SaveToolbarClocks();

    private void HandleSharedResetLayoutRequested(object? sender, EventArgs e)
        => viewModel.ResetLayout();

    private void HandleSharedRefreshSerialPttDevicesRequested(object? sender, EventArgs e)
        => viewModel.RefreshSerialPttDevices();

    private async void HandleSharedApplySerialPttSettingsRequested(object? sender, EventArgs e)
        => await viewModel.ApplySerialPttSettingsAsync();

    private async void HandleSharedApplyGlobalPttKeyRequested(object? sender, EventArgs e)
        => await viewModel.ApplyGlobalPttKeySelectionAsync();

    private async void HandleSharedApplyActiveSystemPttKeyRequested(object? sender, EventArgs e)
        => await viewModel.ApplyActiveSystemPttKeySelectionAsync();

    private async void HandleSharedImportAlertToneRequested(object? sender, EventArgs e)
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

        using IStorageFile file = files[0];
        await using Stream source = await file.OpenReadAsync();
        await viewModel.AddAlertToneAsync(
            file.Name,
            MainWindowViewModel.ResolveAlertMediaType(file.Name),
            source);
    }

    private async void HandleSharedSendAlertToneRequested(object? sender, AlertToneEventArgs e)
    {
        if (e.Tone is AlertToneViewModel tone)
            await viewModel.SendAlertToneAsync(tone);
    }

    private void HandleSharedDeleteAlertToneRequested(object? sender, AlertToneEventArgs e)
    {
        if (e.Tone is AlertToneViewModel tone)
            viewModel.DeleteAlertTone(tone);
    }

    private void HandleSharedSaveWebStreamOutputDeviceRequested(
        object? sender,
        WebStreamRouteSaveEventArgs e)
    {
        if (e.Stream is WebStreamViewModel stream)
            viewModel.SaveWebStreamOutputDevice(stream);
    }

    private void HandleSharedSaveChannelOutputRouteRequested(
        object? sender,
        ChannelAudioRouteEventArgs e)
    {
        if (e.Channel is ChannelViewModel channel)
            viewModel.SaveChannelOutputDevice(channel);
    }

    private void HandleSharedSaveIgnoredSubscribersRequested(
        object? sender,
        RecorderChannelEventArgs e)
    {
        if (e.Channel is ChannelViewModel channel)
            viewModel.TrySaveRecordingIgnoredSubscribers(channel);
    }

    private void HandleClearHistoryFiltersClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearHistoryFilters();

    private void HandleSharedApplyRecordingLocationRequested(object? sender, EventArgs e)
        => viewModel.ApplyRecordingRoot();

    private async void HandleSharedChooseRecordingLocationRequested(object? sender, EventArgs e)
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
        if (sender is Button { Tag: CallRecordingMetadata metadata })
            await ConfirmAndDeleteRecordingAsync(metadata);
    }

    private void HandleSharedHistoryClearFiltersRequested(object? sender, EventArgs e)
        => viewModel.ClearHistoryFilters();

    private void HandleSharedHistoryClearRequested(object? sender, EventArgs e)
        => viewModel.ClearCallHistory();

    private void HandleSharedHistoryExportRequested(object? sender, EventArgs e)
        => HandleExportCallHistoryClick(sender, new RoutedEventArgs());

    private async void HandleSharedHistoryPlayRequested(
        object? sender,
        CallHistoryItemEventArgs e)
    {
        if (e.Item is CallHistoryEntry entry)
            await viewModel.PlayCallHistoryRecordingAsync(entry);
    }

    private async void HandleSharedHistoryStopRequested(object? sender, EventArgs e)
        => await viewModel.StopRecordingPlaybackAsync();

    private void HandleSharedHistoryOpenRequested(
        object? sender,
        CallHistoryItemEventArgs e)
    {
        if (e.Item is CallHistoryEntry { Recording: { } metadata })
            viewModel.OpenRecording(metadata);
    }

    private async void HandleSharedHistoryDeleteRequested(
        object? sender,
        CallHistoryItemEventArgs e)
    {
        if (e.Item is CallHistoryEntry { Recording: { } metadata })
            await ConfirmAndDeleteRecordingAsync(metadata);
    }

    private async Task ConfirmAndDeleteRecordingAsync(CallRecordingMetadata metadata)
    {
        if (await ConfirmAsync(
                "Delete recording",
                $"Delete '{metadata.FileName}' and its catalog metadata? This cannot be undone.",
                "Delete"))
        {
            await viewModel.DeleteRecordingAsync(metadata);
        }
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

    private void HandleSharedSavePatchGroupRequested(object? sender, PatchGroupEventArgs e)
        => viewModel.ApplyPatchGroup(e.Group);

    private async void HandleSharedToggleMultiSelectPttRequested(object? sender, PatchGroupEventArgs e)
        => await viewModel.ToggleMultiSelectPttAsync(e.Group);

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
        if (file is null)
            return;
        using (file)
        await using (Stream destination = await file.OpenWriteAsync())
            viewModel.ExportCallHistory(destination, leaveOpen: true);
    }

    private void HandleClearCallHistoryClick(object? sender, RoutedEventArgs e)
        => viewModel.ClearCallHistory();

    private async void HandleSharedToggleSystemConnectionRequested(
        object? sender,
        ConnectionSystemEventArgs e)
    {
        if (e.System is SystemViewModel system)
            await viewModel.ToggleSystemConnectionAsync(system);
    }

    private async void HandleSharedRestartSystemRequested(
        object? sender,
        ConnectionSystemEventArgs e)
    {
        if (e.System is SystemViewModel system)
            await system.RestartAsync();
    }

    private void HandleCloseClick(object? sender, RoutedEventArgs e) => Close();
}
