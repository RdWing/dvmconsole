using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Presentation;
using System.Text;
using System.ComponentModel;

namespace DvmConsole.Desktop;

internal enum ConfigurationDraftReplacementChoice
{
    Cancel,
    Save,
    Discard
}

public sealed partial class ConfigurationStudioWindow : Window
{
    private readonly MainWindowViewModel runtimeViewModel;
    private readonly UserSettingsStore settingsStore;
    private readonly ManagedConfigurationLibrary configurationLibrary;
    private readonly DesktopConfigurationMaterializer configurationMaterializer;
    private readonly DesktopConfigurationStudioSavePlanner savePlanner;
    private ConfigurationId? managedConfigurationId;
    private bool ready;
    private bool allowClose;
    private int saveOperationInProgress;
    private int deleteSystemOperationInProgress;

    public ConfigurationStudioWindow()
    {
        runtimeViewModel = null!;
        settingsStore = null!;
        configurationLibrary = null!;
        configurationMaterializer = null!;
        savePlanner = null!;
        InitializeComponent();
    }

    internal ConfigurationStudioWindow(
        ConfigurationDocument document,
        MainWindowViewModel runtimeViewModel,
        UserSettingsStore settingsStore,
        ManagedConfigurationLibrary configurationLibrary,
        DesktopConfigurationMaterializer configurationMaterializer,
        ConfigurationId? managedConfigurationId,
        ConfigurationStudioSection initialSection)
    {
        this.runtimeViewModel = runtimeViewModel ?? throw new ArgumentNullException(nameof(runtimeViewModel));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.configurationLibrary = configurationLibrary ?? throw new ArgumentNullException(nameof(configurationLibrary));
        this.configurationMaterializer = configurationMaterializer ?? throw new ArgumentNullException(nameof(configurationMaterializer));
        this.managedConfigurationId = managedConfigurationId;
        InitializeComponent();
        UserSettings initialSettings = settingsStore.Load();
        CodeplugStudioState initialStudioState = managedConfigurationId is { } configurationId &&
            initialSettings.ConfigurationOperatorStates.TryGetValue(
                configurationId.ToString(),
                out ConfigurationOperatorState? configurationState)
            ? configurationState.StudioState.Clone()
            : CodeplugStudioStateStore.Get(initialSettings, document.SourcePath);
        var initialState = new ConfigurationStudioInitialState(
            initialSettings.ChannelWidgetPositions.ToDictionary(
                entry => entry.Key,
                entry => new ConfigurationStudioPosition(entry.Value.X, entry.Value.Y),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(
                initialStudioState.ZoneSystemAssignments,
                StringComparer.OrdinalIgnoreCase),
            initialStudioState.CallPrioritySystemNames.ToArray());
        string documentIdentity = document.SourcePath ??
            managedConfigurationId?.ToString() ??
            $"draft:{Guid.NewGuid():N}";
        DataContext = new ConfigurationStudioViewModel(
            document,
            managedConfigurationId,
            documentIdentity,
            runtimeViewModel,
            new DesktopConfigurationStudioCompanionSource(),
            new DesktopConfigurationStudioPreviewFactory(),
            initialState,
            initialSection);
        savePlanner = new DesktopConfigurationStudioSavePlanner(viewModel, settingsStore);
        foreach (PatchGroupEditorViewModel group in viewModel.OperationalGroups)
            group.PropertyChanged += HandleOperationalGroupPropertyChanged;
        Opened += HandleOpened;
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            foreach (PatchGroupEditorViewModel group in viewModel.OperationalGroups)
                group.PropertyChanged -= HandleOperationalGroupPropertyChanged;
            _ = DiscardManagedDraftAfterCloseAsync();
        };
    }

    public event Func<ConfigurationReference, Task>? ReloadRequested;
    private ConfigurationStudioViewModel viewModel
        => (ConfigurationStudioViewModel)DataContext!;
    internal ConfigurationStudioViewModel StudioViewModel => viewModel;
    internal ConfigurationId? ManagedConfigurationId => managedConfigurationId;
    internal ConfigurationSavePlan CreateSavePlanForCapture(string destinationPath)
        => savePlanner.CreatePlan(destinationPath);
    internal string BuildSaveReviewForCapture(ConfigurationSavePlan plan)
        => savePlanner.BuildReviewText(plan);
    internal Func<string, string, string, Task<bool>>? EditMenuConfirmationOverride { get; set; }
    internal Func<Task<ConfigurationDraftReplacementChoice>>? DraftReplacementChoiceOverride { get; set; }
    internal Func<string, string, string, Task<bool>>? DialogConfirmationOverride { get; set; }
    internal Func<string, string, Task>? MessageOverride { get; set; }
    internal Func<ConfigurationReference, ValueTask<string>>? ConfigurationMaterializationOverride { get; set; }
    internal Func<string, FilePickerFileType, Task<IStorageFile?>>? CompanionFilePickerOverride { get; set; }
    internal Func<string, string, Task<IStorageFile?>>? CodeplugSaveFilePickerOverride { get; set; }
    internal Task<bool> ReviewAndSaveForCaptureAsync(bool saveCopy = false, bool offerReload = true)
        => ReviewAndSaveAsync(saveCopy, offerReload);
    internal Task DeleteSelectedSystemForCaptureAsync()
        => DeleteSelectedSystemAsync();

    internal void FitInitialBoundsToDisplay(Screen? screen)
    {
        if (screen is null)
            return;

        ConfigurationStudioInitialPlacement placement =
            ConfigurationStudioInitialPlacement.FitToWorkingArea(
                new Size(Width, Height),
                screen.WorkingArea,
                screen.Scaling);
        MinWidth = Math.Min(MinWidth, placement.Size.Width);
        MinHeight = Math.Min(MinHeight, placement.Size.Height);
        Width = placement.Size.Width;
        Height = placement.Size.Height;
        Position = placement.Position;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void HandleOpened(object? sender, EventArgs e)
        => ready = true;

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
        if (!viewModel.IsDirty)
        {
            await DiscardManagedDraftAsync();
            CloseForSessionReplacement();
            return true;
        }

        ConfigurationDraftReplacementChoice choice = await ChooseDraftReplacementAsync();
        switch (choice)
        {
            case ConfigurationDraftReplacementChoice.Save:
                if (!await ReviewAndSaveAsync(saveCopy: false, offerReload: false))
                    return false;
                CloseForSessionReplacement();
                return true;
            case ConfigurationDraftReplacementChoice.Discard:
                await DiscardManagedDraftAsync();
                CloseForSessionReplacement();
                return true;
            default:
                return false;
        }
    }

    private async void HandleSharedStudioEditCommandRequested(
        object? sender,
        ConfigurationStudioEditCommandEventArgs e)
        => await ExecuteEditMenuCommandAsync(
            e.Command,
            EditMenuConfirmationOverride,
            e.SelectedChannels);

    internal async Task ExecuteEditMenuCommandAsync(
        ConfigurationStudioEditCommand command,
        Func<string, string, string, Task<bool>>? confirm = null,
        IEnumerable<ChannelConfiguration>? selectedChannels = null)
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
                viewModel.ApplySelectedCardSize(selectedChannels ?? SelectedChannelRows());
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsRxOnly:
                viewModel.SetChannelsRxOnly(selectedChannels ?? SelectedChannelRows(), rxOnly: true);
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsTxCapable:
                viewModel.SetChannelsRxOnly(selectedChannels ?? SelectedChannelRows(), rxOnly: false);
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
    private async void HandleSharedDeleteSystemRequested(object? sender, EventArgs e)
    {
        await DeleteSelectedSystemAsync();
    }

    private async Task DeleteSelectedSystemAsync()
    {
        if (Interlocked.Exchange(ref deleteSystemOperationInProgress, 1) != 0)
            return;

        try
        {
            if (viewModel.SelectedSystem is not { } system)
                return;
            if (await ConfirmAsync(
                    "Delete system",
                    $"Delete '{system.Name}'? Channels that reference it will be reported as errors until reassigned.",
                    "Delete"))
            {
                viewModel.DeleteSystem(system);
            }
        }
        finally
        {
            Volatile.Write(ref deleteSystemOperationInProgress, 0);
        }
    }
    private IEnumerable<ChannelConfiguration> SelectedChannelRows()
        => this.FindControl<ConfigurationStudioView>("studioView")?.ZonesView.GetSelectedChannelRows() ?? [];
    private async void HandleSharedDeleteStreamRequested(object? sender, EventArgs e)
    {
        if (viewModel.SelectedStream is { } row &&
            await ConfirmAsync("Delete web stream", $"Delete '{row.Stream.Name}' from zone '{row.Zone.Name}'?", "Delete"))
            viewModel.DeleteStream();
    }

    private async void HandleSharedDeleteGroupRequested(object? sender, EventArgs e)
    {
        if (viewModel.SelectedGroup is { } group &&
            await ConfirmAsync("Delete group", $"Delete '{group.Name}'? Its codeplug-scoped membership, direction, and enabled state will be removed when saved.", "Delete"))
            viewModel.DeleteGroup();
    }
    private async void HandleSharedDeleteKeyRequested(object? sender, EventArgs e)
    {
        if (viewModel.SelectedKey is { } key &&
            await ConfirmAsync("Delete encryption key", $"Delete {key.Protocol.ToUpperInvariant()} key {key.KeyId}? Channels that reference it may no longer decrypt or transmit securely.", "Delete"))
        {
            viewModel.DeleteKey();
        }
    }
    private async void HandleSharedDeleteAliasRequested(object? sender, EventArgs e)
    {
        if (viewModel.SelectedAlias is { } row &&
            await ConfirmAsync("Delete RID alias", $"Delete RID {row.Alias.Rid} ({row.Alias.Alias}) from its alias file?", "Delete"))
        {
            viewModel.DeleteAlias();
        }
    }

    private async void HandleSharedBrowseKeyFileRequested(object? sender, EventArgs e)
    {
        IStorageFile? file = await PickCompanionFileAsync(
            "Choose encryption key file",
            new FilePickerFileType("Encryption key file")
            {
                Patterns = ["*.clear", "*.yml", "*.yaml"],
                MimeTypes = ["application/yaml", "text/yaml", "text/plain"]
            });
        if (file is null)
            return;

        await ImportSelectedCompanionAsync(
            file,
            (name, content) => viewModel.AttachKeyFile(name, content),
            "The key file could not be added to managed storage.");
    }

    private async void HandleSharedBrowseAliasFileRequested(
        object? sender,
        ConfigurationStudioAliasFileEventArgs e)
    {
        IStorageFile? file = await PickCompanionFileAsync(
            $"Choose RID alias file for {e.System.Name}",
            new FilePickerFileType("RID alias file")
            {
                Patterns = ["*.yml", "*.yaml"],
                MimeTypes = ["application/yaml", "text/yaml", "text/plain"]
            });
        if (file is null)
            return;

        await ImportSelectedCompanionAsync(
            file,
            (name, content) => viewModel.AttachAliasFile(e.System, name, content),
            "The RID alias file could not be added to managed storage.");
    }

    private async Task<IStorageFile?> PickCompanionFileAsync(
        string title,
        FilePickerFileType fileType)
    {
        if (CompanionFilePickerOverride is { } pick)
            return await pick(title, fileType);

        if (!StorageProvider.CanOpen)
        {
            await ShowMessageAsync(
                "File picker unavailable",
                "This platform did not provide an available file picker.");
            return null;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [fileType]
            });
        return files.Count == 0 ? null : files[0];
    }

    private async Task ImportSelectedCompanionAsync(
        IStorageFile file,
        Func<string, string, string> attach,
        string failureMessage)
    {
        try
        {
            string displayName = await AvaloniaStorageThreading.Invoke(() => file.Name);
            await using Stream stream = await AvaloniaStorageThreading.InvokeAsync(file.OpenReadAsync);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string content = await reader.ReadToEndAsync();
            string managedReference = await AvaloniaStorageThreading.Invoke(() => attach(displayName, content));
            await AvaloniaStorageThreading.InvokeAsync(() => ShowMessageAsync(
                "Managed companion added",
                $"{managedReference} is now staged with this configuration. The selected original was not changed."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
            FormatException or YamlDotNet.Core.YamlException)
        {
            DesktopCrashLog.Write("Configuration Studio companion import", exception);
            await AvaloniaStorageThreading.InvokeAsync(() => ShowMessageAsync(
                "Unable to add companion",
                $"{failureMessage}\n\n{exception.Message}"));
        }
        finally
        {
            AvaloniaStorageThreading.Invoke(file.Dispose);
        }
    }

    private void HandleSharedExportFullRequested(object? sender, EventArgs e)
        => HandleExportFullClick(sender, new RoutedEventArgs());

    private void HandleSharedExportSanitizedRequested(object? sender, EventArgs e)
        => HandleExportSanitizedClick(sender, new RoutedEventArgs());

    private void HandleSharedSaveCopyRequested(object? sender, EventArgs e)
        => HandleSaveAsClick(sender, new RoutedEventArgs());

    private void HandleSharedReviewSaveRequested(object? sender, EventArgs e)
        => HandleReviewAndSaveClick(sender, new RoutedEventArgs());

    private async void HandleSharedApplyPatchGroupRequested(object? sender, PatchGroupEventArgs e)
    {
        if (viewModel.CanUseOperationalGroups)
        {
            if (viewModel.ApplyOperationalGroup(e.Group) is { } error)
                await ShowMessageAsync("Group state not applied", error);
        }
    }

    private async void HandleSharedApplyAllOperatorGroupsRequested(object? sender, EventArgs e)
        => await ApplyAllOperatorGroupsAsync(closeAfterApply: false);

    private async void HandleSharedApplyOperatorGroupsAndCloseRequested(object? sender, EventArgs e)
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

    private async void HandleSharedMultiSelectPttRequested(object? sender, PatchGroupEventArgs e)
    {
        if (viewModel.CanUseOperationalGroups)
            await runtimeViewModel.ToggleMultiSelectPttAsync(e.Group);
    }

    private async void HandleReviewAndSaveClick(object? sender, RoutedEventArgs e)
        => await HandleSaveCommandAsync(saveCopy: false);

    private async void HandleSaveAsClick(object? sender, RoutedEventArgs e)
        => await HandleSaveCommandAsync(saveCopy: true);

    private async Task HandleSaveCommandAsync(bool saveCopy)
    {
        try
        {
            await ReviewAndSaveAsync(saveCopy);
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Configuration Studio save command", exception);
            if (!IsVisible)
                return;
            try
            {
                await ShowMessageAsync(
                    "Configuration save failed",
                    $"The save workflow could not finish. Any committed managed revision remains recoverable.\n\n{exception.Message}");
            }
            catch (Exception reportingException)
            {
                DesktopCrashLog.Write("Configuration Studio save error reporting", reportingException);
            }
        }
    }

    private async Task<bool> ReviewAndSaveAsync(bool saveCopy, bool offerReload = true)
    {
        if (Interlocked.CompareExchange(ref saveOperationInProgress, 1, 0) != 0)
            return false;

        try
        {
            return await ReviewAndSaveCoreAsync(saveCopy, offerReload);
        }
        finally
        {
            Volatile.Write(ref saveOperationInProgress, 0);
        }
    }

    private async Task<bool> ReviewAndSaveCoreAsync(bool saveCopy, bool offerReload)
    {
        try
        {
            await runtimeViewModel.FlushUserSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            await ShowMessageAsync(
                "Unable to prepare save",
                $"Current operator settings could not be saved.\n\n{exception.Message}");
            return false;
        }
        ConfigurationSavePlan plan;
        string planPath = viewModel.Document.SourcePath ?? Path.Combine(
            Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
            "ConfigurationDraftPreview",
            "codeplug.yml");
        try
        {
            plan = savePlanner.CreatePlan(planPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Unable to prepare save", exception.Message);
            return false;
        }

        if (!plan.CanSave)
        {
            viewModel.OpenValidationDrawer();
            return false;
        }
        string action = saveCopy ? "Save a copy" : "Save";
        if (!await ConfirmAsync("Review & Save", savePlanner.BuildReviewText(plan), action))
            return false;

        ConfigurationCommit? committed = null;
        try
        {
            string yaml = plan.Files.First(file => file.Category == "Codeplug").Content;
            if (saveCopy)
                yaml = ConfigurationCopyPolicy.RemoveTrustScopedWebAuthorization(yaml);
            ConfigurationDraft draft;
            bool currentConfigurationIsCatalogued = managedConfigurationId is ConfigurationId currentId &&
                await ConfigurationExistsAsync(currentId);
            if ((saveCopy && currentConfigurationIsCatalogued) || managedConfigurationId is null)
            {
                string name = saveCopy ? "Configuration Copy" : "Untitled Configuration";
                draft = await configurationLibrary.CreateDraftAsync(name);
            }
            else
            {
                draft = await configurationLibrary.OpenDraftAsync(managedConfigurationId.Value);
            }

            var companions = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
            foreach (ConfigurationFileChange file in plan.Files.Where(file =>
                         file.Category is not "Codeplug" and not "Operator settings"))
            {
                companions[Path.GetFileName(file.Path)] = Encoding.UTF8.GetBytes(file.Content);
            }
            draft = await configurationLibrary.StageDraftAsync(
                draft with { Yaml = yaml, IsDirty = true },
                companions);
            committed = await configurationLibrary.CommitAsync(draft);

            ConfigurationFileChange[] settingsChanges = plan.Files
                .Where(file => file.Category == "Operator settings")
                .ToArray();
            string backupRoot = Path.Combine(
                Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
                "ConfigurationBackups");
            if (settingsChanges.Length > 0)
                _ = ConfigurationSaveTransaction.Execute(new ConfigurationSavePlan(settingsChanges, []), backupRoot);

            string managedPath = ConfigurationMaterializationOverride is { } materialize
                ? await materialize(committed.Reference)
                : await configurationMaterializer.MaterializeAsync(committed.Reference);
            UserSettings committedSettings = settingsStore.Load();
            if (saveCopy && runtimeViewModel.ConfigurationReference is { } sourceConfiguration)
            {
                ConfigurationOperatorStateStore.Copy(
                    committedSettings,
                    sourceConfiguration.Id.ToString(),
                    committed.Reference.Id.ToString(),
                    includeWebStreamAuthorization: false);
            }
            CodeplugGroupState copiedGroupState =
                CodeplugGroupStateStore.CopyForSaveAs(committedSettings, planPath, managedPath);
            CodeplugStudioState copiedStudioState =
                CodeplugStudioStateStore.CopyForSaveAs(committedSettings, planPath, managedPath);
            ConfigurationOperatorStateStore.UpdateDocumentState(
                committedSettings,
                committed.Reference.Id.ToString(),
                copiedGroupState,
                copiedStudioState);
            settingsStore.Save(committedSettings);
            Exception? settingsPersistenceFailure = null;
            try
            {
                await runtimeViewModel.AdoptUserSettingsSnapshotAsync(
                    settingsStore.CaptureSnapshot(committedSettings));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                settingsPersistenceFailure = exception;
            }
            viewModel.AcceptSaved(managedPath, committed.Reference.Id, plan);
            managedConfigurationId = committed.Reference.Id;
            await ShowMessageAsync(
                "Configuration saved",
                saveCopy
                    ? "Saved a managed copy with a new configuration ID." +
                      DescribeSettingsPersistenceWarning(settingsPersistenceFailure)
                    : "Committed a new immutable managed revision." +
                      DescribeSettingsPersistenceWarning(settingsPersistenceFailure));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            if (committed is null)
            {
                await ShowMessageAsync(
                    "Configuration save failed",
                    $"No managed revision was committed. Restricted backups remain available when originals were staged.\n\n{exception.Message}");
                return false;
            }

            managedConfigurationId = committed.Reference.Id;
            await ShowMessageAsync(
                "Configuration committed with a follow-up failure",
                $"Managed revision {committed.Reference.Revision} was committed and remains recoverable in the Configuration Library, " +
                "but Studio could not finish materializing it or rebasing operator settings. Reopen the managed configuration before making more edits.\n\n" +
                exception.Message);
            return true;
        }

        ConfigurationCommit commit = committed;

        bool updatesActiveConfiguration =
            runtimeViewModel.ConfigurationReference?.Id == commit.Reference.Id;
        if (offerReload &&
            !saveCopy &&
            await ConfirmAsync(
                updatesActiveConfiguration
                    ? "Reload active configuration?"
                    : "Load saved configuration?",
                updatesActiveConfiguration
                    ? "The running FNE sessions still use the previous managed revision. Disconnect and reload now, or cancel to keep the new revision pending without changing the active session."
                    : "This configuration is saved, but the console is still displaying a different configuration. Disconnect that configuration and load this one now, or cancel to leave this saved configuration in the library.",
                updatesActiveConfiguration
                    ? "Disconnect and reload"
                    : "Disconnect and load"))
        {
            if (ReloadRequested is not { } reload)
            {
                await ShowMessageAsync(
                    "Reload unavailable",
                    "The managed revision was saved, but this Studio window is no longer attached to the running console. Reopen it from the active console and reload the pending revision.");
                return false;
            }

            const string normalTitle = "DVM Console Configuration Studio";
            Title = $"{normalTitle} — Disconnecting and reloading…";
            ConfigurationStudioView? reloadingView = this.FindControl<ConfigurationStudioView>("studioView");
            if (reloadingView is null)
            {
                await ShowMessageAsync(
                    "Reload unavailable",
                    "The managed revision was saved, but Configuration Studio is no longer attached to its editor view.");
                return false;
            }
            reloadingView.IsEnabled = false;
            try
            {
                await reload(commit.Reference);
            }
            finally
            {
                if (IsVisible)
                {
                    reloadingView.IsEnabled = true;
                    Title = normalTitle;
                }
            }
        }
        return true;
    }

    private static string DescribeSettingsPersistenceWarning(Exception? failure)
        => failure is null
            ? string.Empty
            : "\n\nThe managed revision was committed and the live settings were rebased, " +
              $"but the settings writer reported a failure and will retry after the next change.\n\n{failure.Message}";

    private async void HandleExportFullClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "Export full interoperable copy",
                "This copy includes FNE credentials, transport secrets, stream credentials, operational addresses, and references to local key material. Store and share it as a secret.",
                "Choose destination"))
            return;
        IStorageFile? file = await PickCodeplugSaveFileAsync("Export full interoperable copy");
        if (file is not null)
            await WriteExportAsync(file, sanitized: false);
    }

    private async void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
    {
        IStorageFile? file = await PickCodeplugSaveFileAsync(
            "Export sanitized support copy",
            "dvmconsole-support-sanitized.yml");
        if (file is not null)
            await WriteExportAsync(file, sanitized: true);
    }

    private async Task WriteExportAsync(IStorageFile file, bool sanitized)
    {
        string displayName = await AvaloniaStorageThreading.Invoke(() => file.Name);
        try
        {
            string sourcePath = viewModel.Document.SourcePath ?? Path.Combine(
                Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
                "ConfigurationDraftPreview",
                "codeplug.yml");
            var materializedSource = new DesktopConfigurationDocumentSet(sourcePath);
            var source = new ConfigurationStudioExportDocumentSet(
                materializedSource,
                viewModel.CaptureExportCompanionContents());
            using AvaloniaStorageConfigurationDocumentSet destination = await AvaloniaStorageThreading.Invoke(
                () => new AvaloniaStorageConfigurationDocumentSet(file));
            ConfigurationBundleExportResult result = await ConfigurationBundleExporter.ExportAsync(
                viewModel.FullExportText,
                source,
                destination,
                new ConfigurationExportOptions(
                    Sanitized: sanitized,
                    IncludeCompanions: !sanitized));
            string[] omitted = result.OmittedCompanionReferences.ToArray();
            string title = omitted.Length == 0
                ? "Export complete"
                : "Export complete with omitted files";
            string message = omitted.Length == 0
                ? displayName
                : $"{displayName}\n\nThe YAML was exported, but these referenced companion files were not found and could not be copied:\n\n" +
                  string.Join(Environment.NewLine, omitted.Select(reference => $"• {reference}"));
            await AvaloniaStorageThreading.InvokeAsync(() => ShowMessageAsync(title, message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DesktopCrashLog.Write("Configuration Studio export", exception);
            await AvaloniaStorageThreading.InvokeAsync(() => ShowMessageAsync("Export failed", exception.Message));
        }
    }

    private async Task<IStorageFile?> PickCodeplugSaveFileAsync(
        string title,
        string suggestedName = "codeplug.yml")
    {
        if (CodeplugSaveFilePickerOverride is { } pick)
            return await pick(title, suggestedName);

        if (!StorageProvider.CanSave)
        {
            await ShowMessageAsync(
                "File picker unavailable",
                "This platform did not provide an available save-file picker.");
            return null;
        }
        return await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (DialogConfirmationOverride is { } confirm)
            return await confirm(title, message, confirmLabel);

        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) => { confirmed = true; parts.Window.Close(); };
        await parts.Window.ShowDialog(this);
        return confirmed;
    }

    private async Task<ConfigurationDraftReplacementChoice> ChooseDraftReplacementAsync()
    {
        if (DraftReplacementChoiceOverride is { } choose)
            return await choose();

        ConfigurationDraftReplacementChoice choice = ConfigurationDraftReplacementChoice.Cancel;
        OperatorDialogParts parts = OperatorDialogFactory.CreateChoice(
            "Save configuration draft?",
            "Starting another configuration closes this Studio draft. Save it first, discard it, or cancel and keep editing.",
            "Save",
            "Discard");
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.SecondaryButton!.Click += (_, _) =>
        {
            choice = ConfigurationDraftReplacementChoice.Discard;
            parts.Window.Close();
        };
        parts.PrimaryButton.Click += (_, _) =>
        {
            choice = ConfigurationDraftReplacementChoice.Save;
            parts.Window.Close();
        };
        await parts.Window.ShowDialog(this);
        return choice;
    }

    private async Task<ConfigurationDraftReplacementChoice> ChooseCloseDraftAsync()
    {
        if (DraftReplacementChoiceOverride is { } choose)
            return await choose();

        ConfigurationDraftReplacementChoice choice = ConfigurationDraftReplacementChoice.Cancel;
        OperatorDialogParts parts = OperatorDialogFactory.CreateChoice(
            "Save configuration draft?",
            "This draft has changes that have not been saved. Save it before closing, discard it, or cancel and keep editing.",
            "Save",
            "Discard");
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.SecondaryButton!.Click += (_, _) =>
        {
            choice = ConfigurationDraftReplacementChoice.Discard;
            parts.Window.Close();
        };
        parts.PrimaryButton.Click += (_, _) =>
        {
            choice = ConfigurationDraftReplacementChoice.Save;
            parts.Window.Close();
        };
        await parts.Window.ShowDialog(this);
        return choice;
    }

    private async ValueTask DiscardManagedDraftAsync()
    {
        if (managedConfigurationId is ConfigurationId id)
            await configurationLibrary.DiscardDraftAsync(id);
    }

    private async Task DiscardManagedDraftAfterCloseAsync()
    {
        try
        {
            await DiscardManagedDraftAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DesktopCrashLog.Write("Configuration Studio draft cleanup", exception);
        }
    }

    private async ValueTask<bool> ConfigurationExistsAsync(ConfigurationId id)
    {
        await foreach (ConfigurationSummary summary in configurationLibrary.ListAsync())
        {
            if (summary.Id == id)
                return true;
        }
        return false;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (MessageOverride is { } show)
        {
            await show(title, message);
            return;
        }

        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage(title, message, "OK");
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || !viewModel.IsDirty)
            return;
        e.Cancel = true;
        ConfigurationDraftReplacementChoice choice = await ChooseCloseDraftAsync();
        if (choice == ConfigurationDraftReplacementChoice.Save)
        {
            if (!await ReviewAndSaveAsync(saveCopy: false))
                return;
            allowClose = true;
            Close();
        }
        else if (choice == ConfigurationDraftReplacementChoice.Discard)
        {
            await DiscardManagedDraftAsync();
            allowClose = true;
            Close();
        }
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
