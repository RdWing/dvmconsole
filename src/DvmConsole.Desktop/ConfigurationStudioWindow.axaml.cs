using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using System.ComponentModel;

namespace DvmConsole.Desktop;

internal enum ConfigurationStudioEditCommand
{
    AddChannel,
    DuplicateChannel,
    DeleteChannel,
    MoveChannelUp,
    MoveChannelDown,
    ApplySelectedCardSize,
    SetSelectedRowsRxOnly,
    SetSelectedRowsTxCapable,
    AddZone,
    DuplicateZone,
    DeleteZone
}

public sealed partial class ConfigurationStudioWindow : Window
{
    private readonly MainWindowViewModel runtimeViewModel;
    private readonly UserSettingsStore settingsStore;
    private bool ready;
    private bool allowClose;
    private bool isClosed;
    private bool handlingKeySelectionChange;
    private int queuedSelectionCommitVersion;
    private int queuedChannelScrollVersion;
    private Control? draggedCard;
    private ConfigurationChannelPreviewViewModel? draggedPreview;
    private Point dragOrigin;
    private double dragX;
    private double dragY;

    public ConfigurationStudioWindow()
    {
        runtimeViewModel = null!;
        settingsStore = null!;
        InitializeComponent();
    }

    public ConfigurationStudioWindow(
        ConfigurationDocument document,
        MainWindowViewModel runtimeViewModel,
        UserSettingsStore settingsStore,
        ConfigurationStudioSection initialSection)
    {
        this.runtimeViewModel = runtimeViewModel ?? throw new ArgumentNullException(nameof(runtimeViewModel));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        InitializeComponent();
        DataContext = new ConfigurationStudioViewModel(document, runtimeViewModel, settingsStore, initialSection);
        viewModel.PropertyChanged += HandleStudioPropertyChanged;
        foreach (PatchGroupEditorViewModel group in viewModel.OperationalGroups)
            group.PropertyChanged += HandleOperationalGroupPropertyChanged;
        Opened += HandleOpened;
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            isClosed = true;
            queuedSelectionCommitVersion++;
            queuedChannelScrollVersion++;
            viewModel.PropertyChanged -= HandleStudioPropertyChanged;
            foreach (PatchGroupEditorViewModel group in viewModel.OperationalGroups)
                group.PropertyChanged -= HandleOperationalGroupPropertyChanged;
        };
    }

    public event Func<string, Task>? ReloadRequested;
    private ConfigurationStudioViewModel viewModel
        => (ConfigurationStudioViewModel)DataContext!;
    internal ConfigurationStudioViewModel StudioViewModel => viewModel;
    internal Func<string, string, string, Task<bool>>? EditMenuConfirmationOverride { get; set; }

    private void HandleOpened(object? sender, EventArgs e)
    {
        ready = true;
        QueueSelectedChannelScroll();
    }

    private void HandleStudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationStudioViewModel.SelectedChannelRow))
            QueueSelectedChannelScroll();
    }

    private void QueueSelectedChannelScroll()
    {
        int version = ++queuedChannelScrollVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (isClosed || version != queuedChannelScrollVersion ||
                !viewModel.IsZones || viewModel.SelectedChannelRow is not { } row)
            {
                return;
            }

            this.FindControl<ListBox>("channelList")?.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }

    public void SelectSection(ConfigurationStudioSection section)
    {
        viewModel.SelectSection(section);
        Activate();
    }

    public void CloseForSessionReplacement()
    {
        allowClose = true;
        Close();
    }

    public async Task<bool> ConfirmSessionReplacementAsync()
    {
        if (!viewModel.IsDirty || await ConfirmAsync(
                "Discard configuration draft?",
                "Loading another codeplug closes Configuration Studio. Discard the unsaved draft and continue?",
                "Discard and continue"))
        {
            CloseForSessionReplacement();
            return true;
        }
        return false;
    }

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (!ready)
            return;
        if (e is SelectionChangedEventArgs)
            QueueSelectionCommit(viewModel.CommitFieldEdit);
        else
            viewModel.CommitFieldEdit();
    }

    private void HandleKeyFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (ready)
            viewModel.CommitKeyEdit();
    }

    private void HandleChannelModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ready)
            QueueSelectionCommit(viewModel.CommitChannelModeEdit);
    }

    private void HandleChannelAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ready)
            QueueSelectionCommit(viewModel.CommitChannelAlgorithmEdit);
    }

    private void HandleKeyProtocolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ready || handlingKeySelectionChange || sender is not ComboBox { SelectedItem: ConfigurationProtocolOption })
            return;
        QueueSelectionCommit(() =>
        {
            handlingKeySelectionChange = true;
            try
            {
                viewModel.CommitKeyProtocolEdit();
            }
            finally
            {
                handlingKeySelectionChange = false;
            }
        });
    }

    private void HandleKeyAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ready || handlingKeySelectionChange ||
            sender is not ComboBox { SelectedItem: EncryptionAlgorithmOption })
            return;
        QueueSelectionCommit(() =>
        {
            handlingKeySelectionChange = true;
            try
            {
                viewModel.CommitKeyEdit();
            }
            finally
            {
                handlingKeySelectionChange = false;
            }
        });
    }

    private void HandleAliasFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (ready)
            viewModel.CommitAliasEdit();
    }

    private void HandleUndoClick(object? sender, RoutedEventArgs e) => viewModel.Undo();
    private void HandleRedoClick(object? sender, RoutedEventArgs e) => viewModel.Redo();
    private void HandleSectionNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionName } &&
            Enum.TryParse(sectionName, ignoreCase: true, out ConfigurationStudioSection section))
            viewModel.SelectSection(section);
    }
    private void HandleToggleValidationDrawerClick(object? sender, RoutedEventArgs e)
        => viewModel.IsValidationDrawerOpen = !viewModel.IsValidationDrawerOpen && viewModel.HasValidationIssues;
    private void HandleValidationIssueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConfigurationValidationIssue issue })
            viewModel.NavigateToIssue(issue);
    }
    private async void HandleEditMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string commandName } &&
            Enum.TryParse(commandName, ignoreCase: false, out ConfigurationStudioEditCommand command))
        {
            await ExecuteEditMenuCommandAsync(command, EditMenuConfirmationOverride);
        }
    }

    internal async Task ExecuteEditMenuCommandAsync(
        ConfigurationStudioEditCommand command,
        Func<string, string, string, Task<bool>>? confirm = null)
    {
        confirm ??= ConfirmAsync;
        switch (command)
        {
            case ConfigurationStudioEditCommand.AddChannel:
                viewModel.AddChannel();
                break;
            case ConfigurationStudioEditCommand.DuplicateChannel:
                viewModel.DuplicateChannel();
                break;
            case ConfigurationStudioEditCommand.DeleteChannel:
                if (viewModel.SelectedChannel is { } channel &&
                    await confirm(
                        "Delete channel",
                        $"Delete '{channel.Name}'? Saved widget and group references to this channel will be removed when the draft is saved.",
                        "Delete"))
                {
                    viewModel.DeleteChannel();
                }
                break;
            case ConfigurationStudioEditCommand.MoveChannelUp:
                viewModel.MoveChannel(-1);
                break;
            case ConfigurationStudioEditCommand.MoveChannelDown:
                viewModel.MoveChannel(1);
                break;
            case ConfigurationStudioEditCommand.ApplySelectedCardSize:
                viewModel.ApplySelectedCardSize(SelectedChannelRows());
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsRxOnly:
                viewModel.SetChannelsRxOnly(SelectedChannelRows(), rxOnly: true);
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsTxCapable:
                viewModel.SetChannelsRxOnly(SelectedChannelRows(), rxOnly: false);
                break;
            case ConfigurationStudioEditCommand.AddZone:
                viewModel.AddZone();
                break;
            case ConfigurationStudioEditCommand.DuplicateZone:
                viewModel.DuplicateZone();
                break;
            case ConfigurationStudioEditCommand.DeleteZone:
                if (viewModel.SelectedZone is { } zone &&
                    await confirm(
                        "Delete zone",
                        $"Delete '{zone.Name}' and its {zone.Channels.Count} channel(s) and {zone.WebStreams.Count} stream(s)?",
                        "Delete"))
                {
                    viewModel.DeleteZone();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
    private void HandleAddSystemClick(object? sender, RoutedEventArgs e) => viewModel.AddSystem();
    private void HandleDuplicateSystemClick(object? sender, RoutedEventArgs e) => viewModel.DuplicateSystem();
    private async void HandleDeleteSystemClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedSystem is { } system &&
            await ConfirmAsync("Delete system", $"Delete '{system.Name}'? Channels that reference it will be reported as errors until reassigned.", "Delete"))
            viewModel.DeleteSystem();
    }
    private IEnumerable<ChannelConfiguration> SelectedChannelRows()
        => this.FindControl<ListBox>("channelList")?.SelectedItems?
            .OfType<ConfigurationChannelRow>()
            .Select(row => row.Channel) ?? [];
    private void HandleCardSizeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string cardSize } && viewModel.SelectedChannel is { } channel)
        {
            channel.CardSize = cardSize;
            viewModel.CommitFieldEdit();
        }
    }
    private void HandleResourceColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color } && viewModel.SelectedChannel is { } channel)
        {
            channel.ResourceColor = color;
            viewModel.CommitFieldEdit();
        }
    }
    private void HandleAddStreamClick(object? sender, RoutedEventArgs e) => viewModel.AddStream();
    private async void HandleDeleteStreamClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedStream is { } row &&
            await ConfirmAsync("Delete web stream", $"Delete '{row.Stream.Name}' from zone '{row.Zone.Name}'?", "Delete"))
            viewModel.DeleteStream();
    }
    private void HandleAddGroupClick(object? sender, RoutedEventArgs e) => viewModel.AddGroup();
    private async void HandleDeleteGroupClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedGroup is { } group &&
            await ConfirmAsync("Delete group", $"Delete '{group.Name}'? Its codeplug-scoped membership, direction, and enabled state will be removed when saved.", "Delete"))
            viewModel.DeleteGroup();
    }
    private void HandleAddKeyClick(object? sender, RoutedEventArgs e) => viewModel.AddKey();
    private async void HandleDeleteKeyClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedKey is { } key &&
            await ConfirmAsync("Delete encryption key", $"Delete {key.Protocol.ToUpperInvariant()} key {key.KeyId}? Channels that reference it may no longer decrypt or transmit securely.", "Delete"))
            viewModel.DeleteKey();
    }
    private void HandleAddAliasClick(object? sender, RoutedEventArgs e) => viewModel.AddAlias();
    private async void HandleDeleteAliasClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedAlias is { } row &&
            await ConfirmAsync("Delete RID alias", $"Delete RID {row.Alias.Rid} ({row.Alias.Alias}) from its alias file?", "Delete"))
            viewModel.DeleteAlias();
    }

    private void HandleStreamZoneChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ready && sender is ComboBox { SelectedItem: ZoneConfiguration zone })
            QueueSelectionCommit(() => viewModel.MoveSelectedStreamTo(zone));
    }

    private void HandleZoneSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ready && sender is ComboBox { SelectedItem: SystemConfiguration })
            QueueSelectionCommit(viewModel.CommitZoneSystemEdit);
    }

    private void QueueSelectionCommit(Action commit)
    {
        int version = ++queuedSelectionCommitVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!isClosed && version == queuedSelectionCommitVersion)
                commit();
        }, DispatcherPriority.Background);
    }

    private async void HandleApplyPatchGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group } && viewModel.CanUseOperationalGroups)
        {
            if (viewModel.ApplyOperationalGroup(group) is { } error)
                await ShowMessageAsync("Group state not applied", error);
        }
    }

    private async void HandleApplyAllOperatorGroupsClick(object? sender, RoutedEventArgs e)
        => await ApplyAllOperatorGroupsAsync(closeAfterApply: false);

    private async void HandleApplyOperatorGroupsAndCloseClick(object? sender, RoutedEventArgs e)
        => await ApplyAllOperatorGroupsAsync(closeAfterApply: true);

    private async Task ApplyAllOperatorGroupsAsync(bool closeAfterApply)
    {
        if (viewModel.ApplyAllOperationalGroups() is { } error)
        {
            await ShowMessageAsync("Group state not applied", error);
            return;
        }

        try
        {
            await runtimeViewModel.FlushUserSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            await ShowMessageAsync(
                "Operator state not saved",
                $"The active console was updated, but the operator settings could not be saved.\n\n{exception.Message}");
            return;
        }

        if (!closeAfterApply)
            return;

        if (viewModel.IsDirty && !await ConfirmAsync(
                "Discard YAML draft?",
                "The operator group changes have been applied without reconnecting. Close Configuration Studio and discard the separate unsaved YAML changes?",
                "Discard YAML draft and close"))
        {
            return;
        }

        allowClose = true;
        Close();
    }

    private void HandleOperationalGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ready && e.PropertyName == nameof(PatchGroupEditorViewModel.IsEnabled) &&
            sender is PatchGroupEditorViewModel group && viewModel.CanUseOperationalGroups)
            viewModel.SetOperationalGroupEnabled(group);
    }

    private async void HandleMultiSelectPttClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PatchGroupEditorViewModel group } && viewModel.CanUseOperationalGroups)
            await runtimeViewModel.ToggleMultiSelectPttAsync(group);
    }

    private void HandlePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigurationChannelPreviewViewModel preview } control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;
        draggedCard = control;
        draggedPreview = preview;
        dragOrigin = e.GetPosition(this);
        dragX = preview.X;
        dragY = preview.Y;
        viewModel.BeginPreviewMove();
        viewModel.SelectedChannel = preview.Channel;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void HandlePreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (draggedCard is null || draggedPreview is null || !ReferenceEquals(sender, draggedCard))
            return;
        Point current = e.GetPosition(this);
        viewModel.MovePreviewChannel(
            draggedPreview,
            dragX + (current.X - dragOrigin.X),
            dragY + (current.Y - dragOrigin.Y));
        e.Handled = true;
    }

    private void HandlePreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (draggedCard is null || !ReferenceEquals(sender, draggedCard))
            return;
        e.Pointer.Capture(null);
        ClearPreviewDrag();
        e.Handled = true;
    }

    private void HandlePreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (draggedCard is not null && ReferenceEquals(sender, draggedCard))
            ClearPreviewDrag();
    }

    private void ClearPreviewDrag()
    {
        viewModel.CommitPreviewMove();
        draggedCard = null;
        draggedPreview = null;
    }

    private async void HandleReviewAndSaveClick(object? sender, RoutedEventArgs e)
    {
        string? path = viewModel.Document.SourcePath;
        if (path is null)
            path = await PickCodeplugSavePathAsync("Save configuration");
        if (path is not null)
            await ReviewAndSaveAsync(path);
    }

    private async void HandleSaveAsClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickCodeplugSavePathAsync("Save configuration as");
        if (path is not null)
            await ReviewAndSaveAsync(path);
    }

    private async Task ReviewAndSaveAsync(string path)
    {
        await runtimeViewModel.FlushUserSettingsAsync();
        ConfigurationSavePlan plan;
        try
        {
            plan = viewModel.CreateSavePlan(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Unable to prepare save", exception.Message);
            return;
        }

        if (!plan.CanSave)
        {
            viewModel.OpenValidationDrawer();
            return;
        }
        if (!await ConfirmAsync("Review & Save", viewModel.BuildReviewText(plan), "Save"))
            return;

        try
        {
            string backupRoot = Path.Combine(
                Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
                "ConfigurationBackups");
            ConfigurationSaveResult result = ConfigurationSaveTransaction.Execute(plan, backupRoot);
            viewModel.AcceptSaved(path, plan);
            await ShowMessageAsync(
                "Configuration saved",
                $"Saved {result.WrittenFiles.Count} file(s).\n\nBackup location:\n{result.BackupDirectory}");
        }
        catch (ConfigurationExternalChangeException exception)
        {
            if (await ConfirmAsync(
                    "File changed outside Studio",
                    $"{exception.ChangedPath}\n\nReload the file before continuing, or save this draft to a different path.",
                    "Save a copy"))
            {
                string? copy = await PickCodeplugSavePathAsync("Save conflicted draft as a copy");
                if (copy is not null)
                    await ReviewAndSaveAsync(copy);
            }
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ShowMessageAsync("Configuration save failed", $"No partial save was kept. Restricted backups remain available when originals were staged.\n\n{exception.Message}");
            return;
        }

        if (runtimeViewModel.CurrentCodeplugPath is not null &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(runtimeViewModel.CurrentCodeplugPath), StringComparison.OrdinalIgnoreCase) &&
            await ConfirmAsync(
                "Reload active configuration?",
                "The running FNE sessions still use the previous topology. Disconnect and reload now, or cancel to keep the saved file without changing the active session.",
                "Disconnect and reload"))
        {
            if (ReloadRequested is { } reload)
                await reload(path);
        }
    }

    private async void HandleExportFullClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "Export full interoperable copy",
                "This copy includes FNE credentials, transport secrets, stream credentials, operational addresses, and references to local key material. Store and share it as a secret.",
                "Choose destination"))
            return;
        string? path = await PickCodeplugSavePathAsync("Export full interoperable copy");
        if (path is not null)
            await WriteExportAsync(path, viewModel.FullExportText);
    }

    private async void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickCodeplugSavePathAsync("Export sanitized support copy", "dvmconsole-support-sanitized.yml");
        if (path is not null)
            await WriteExportAsync(path, viewModel.Document.SerializeSanitized());
    }

    private async Task WriteExportAsync(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
            await ShowMessageAsync("Export complete", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Export failed", exception.Message);
        }
    }

    private async Task<string?> PickCodeplugSavePathAsync(string title, string suggestedName = "codeplug.yml")
    {
        if (!StorageProvider.CanSave)
            return null;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "yml",
            FileTypeChoices =
            [
                new FilePickerFileType("YAML codeplug")
                {
                    Patterns = ["*.yml", "*.yaml"],
                    MimeTypes = ["application/yaml", "text/yaml", "text/plain"]
                }
            ]
        });
        return file?.TryGetLocalPath();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) => { confirmed = true; parts.Window.Close(); };
        await parts.Window.ShowDialog(this);
        return confirmed;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage(title, message, "OK");
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || !viewModel.IsDirty)
            return;
        e.Cancel = true;
        if (await ConfirmAsync(
                "Discard configuration draft?",
                "This draft has changes that have not been saved. Close Configuration Studio and discard them?",
                "Discard draft"))
        {
            allowClose = true;
            Close();
        }
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
