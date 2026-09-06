// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
using System.ComponentModel;
using System.Text;

namespace DvmConsole.Desktop;

internal enum ConfigurationDraftReplacementChoice
{
    Cancel,
    Save,
    Discard
}

internal sealed record CompanionFileSelection(string Name, string Content);

public sealed partial class ConfigurationStudioWindow : Window
{
    private readonly MainWindowViewModel runtimeViewModel;
    private readonly UserSettingsStore settingsStore;
    private readonly ManagedConfigurationLibrary configurationLibrary;
    private readonly DesktopConfigurationMaterializer configurationMaterializer;
    private readonly ConfigurationStudioSessionController sessionController;
    private readonly ConfigurationStudioDocumentController documentController;
    private bool ready;
    private bool allowClose;
    private int saveOperationInProgress;

    public ConfigurationStudioWindow()
    {
        runtimeViewModel = null!;
        settingsStore = null!;
        configurationLibrary = null!;
        configurationMaterializer = null!;
        sessionController = null!;
        documentController = null!;
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
        var savePlanner = new DesktopConfigurationStudioSavePlanner(viewModel, settingsStore);
        sessionController = new ConfigurationStudioSessionController(
            viewModel,
            new ConfigurationStudioRuntimePorts(
                runtimeViewModel.FlushUserSettingsAsync,
                () => runtimeViewModel.ConfigurationReference,
                runtimeViewModel.AdoptUserSettingsSnapshotAsync),
            settingsStore,
            configurationLibrary,
            savePlanner,
            managedConfigurationId);
        documentController = new ConfigurationStudioDocumentController(viewModel);
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
    internal ConfigurationId? ManagedConfigurationId => sessionController.ManagedConfigurationId;
    internal ConfigurationSavePlan CreateSavePlanForCapture(string destinationPath)
        => sessionController.CreatePlan(destinationPath);
    internal string BuildSaveReviewForCapture(ConfigurationSavePlan plan)
        => sessionController.BuildReviewText(plan);
    internal Func<string, string, string, Task<bool>>? EditMenuConfirmationOverride { get; set; }
    internal Func<Task<ConfigurationDraftReplacementChoice>>? DraftReplacementChoiceOverride { get; set; }
    internal Func<string, string, string, Task<bool>>? DialogConfirmationOverride { get; set; }
    internal Func<string, string, Task>? MessageOverride { get; set; }
    internal Func<ConfigurationReference, ValueTask<IConfigurationMaterializationLease>>?
        ConfigurationMaterializationOverride
    { get; set; }
    internal Func<string, FilePickerFileType, Task<CompanionFileSelection?>>? CompanionFilePickerOverride { get; set; }
    internal Func<string, string, Task<string?>>? CodeplugSavePathOverride { get; set; }
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
        await documentController.ExecuteAsync(
            command,
            (title, message, confirmLabel) => confirm(title, message, confirmLabel),
            selectedChannels ?? SelectedChannelRows());
    }
    private async void HandleSharedDeleteSystemRequested(object? sender, EventArgs e)
    {
        await DeleteSelectedSystemAsync();
    }

    private async Task DeleteSelectedSystemAsync()
        => await documentController.DeleteSelectedSystemAsync(ConfirmAsync);
    private IEnumerable<ChannelConfiguration> SelectedChannelRows()
        => this.FindControl<ConfigurationStudioView>("studioView")?.ZonesView.GetSelectedChannelRows() ?? [];
    private async void HandleSharedDeleteStreamRequested(object? sender, EventArgs e)
        => await documentController.DeleteSelectedStreamAsync(ConfirmAsync);

    private async void HandleSharedDeleteGroupRequested(object? sender, EventArgs e)
        => await documentController.DeleteSelectedGroupAsync(ConfirmAsync);
    private async void HandleSharedDeleteKeyRequested(object? sender, EventArgs e)
        => await documentController.DeleteSelectedKeyAsync(ConfirmAsync);
    private async void HandleSharedDeleteAliasRequested(object? sender, EventArgs e)
        => await documentController.DeleteSelectedAliasAsync(ConfirmAsync);

    private async void HandleSharedBrowseKeyFileRequested(object? sender, EventArgs e)
    {
        await PickAndImportCompanionAsync(
            "Choose encryption key file",
            new FilePickerFileType("Encryption key file")
            {
                Patterns = ["*.clear", "*.yml", "*.yaml"],
                MimeTypes = ["application/yaml", "text/yaml", "text/plain"]
            },
            (name, content) => viewModel.AttachKeyFile(name, content),
            "The key file could not be added to managed storage.");
    }

    private async void HandleSharedBrowseAliasFileRequested(
        object? sender,
        ConfigurationStudioAliasFileEventArgs e)
    {
        await PickAndImportCompanionAsync(
            $"Choose RID alias file for {e.System.Name}",
            new FilePickerFileType("RID alias file")
            {
                Patterns = ["*.yml", "*.yaml"],
                MimeTypes = ["application/yaml", "text/yaml", "text/plain"]
            },
            (name, content) => viewModel.AttachAliasFile(e.System, name, content),
            "The RID alias file could not be added to managed storage.");
    }

    private async Task<CompanionFileSelection?> PickCompanionFileAsync(
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
        if (files.Count == 0)
            return null;

        IStorageFile file = files[0];
        try
        {
            string displayName = await AvaloniaStorageThreading.Invoke(() => file.Name);
            await using Stream stream = await AvaloniaStorageThreading.InvokeAsync(file.OpenReadAsync);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new CompanionFileSelection(displayName, await reader.ReadToEndAsync());
        }
        finally
        {
            AvaloniaStorageThreading.Invoke(file.Dispose);
        }
    }

    private async Task PickAndImportCompanionAsync(
        string title,
        FilePickerFileType fileType,
        Func<string, string, string> attach,
        string failureMessage)
    {
        try
        {
            CompanionFileSelection? selection = await PickCompanionFileAsync(title, fileType);
            if (selection is null)
                return;

            string managedReference = await AvaloniaStorageThreading.Invoke(
                () => attach(selection.Name, selection.Content));
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
        var ports = new ConfigurationStudioSessionPorts(
            ConfirmAsync,
            ShowMessageAsync,
            reference => ConfigurationMaterializationOverride is { } materialize
                ? materialize(reference)
                : configurationMaterializer.MaterializeAsync(reference),
            ReloadSavedConfigurationAsync);
        return await sessionController.ReviewAndSaveAsync(saveCopy, offerReload, ports);
    }

    private async Task<bool> ReloadSavedConfigurationAsync(ConfigurationReference reference)
    {
        if (ReloadRequested is not { } reload)
        {
            await ShowMessageAsync(
                "Reload unavailable",
                "The managed revision was saved, but this Studio window is no longer attached to the running console. Reopen it from the active console and reload the pending revision.");
            return false;
        }

        const string normalTitle = "DVM Console Configuration Studio";
        ConfigurationStudioView? reloadingView = this.FindControl<ConfigurationStudioView>("studioView");
        if (reloadingView is null)
        {
            await ShowMessageAsync(
                "Reload unavailable",
                "The managed revision was saved, but Configuration Studio is no longer attached to its editor view.");
            return false;
        }

        Title = $"{normalTitle} — Disconnecting and reloading…";
        reloadingView.IsEnabled = false;
        try
        {
            await reload(reference);
            return true;
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

    private async void HandleExportFullClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "Export full interoperable copy",
                "This copy includes FNE credentials, transport secrets, stream credentials, operational addresses, and references to local key material. Store and share it as a secret.",
                "Choose destination"))
            return;
        await PickAndWriteExportAsync("Export full interoperable copy", "codeplug.yml", sanitized: false);
    }

    private async void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
    {
        await PickAndWriteExportAsync(
            "Export sanitized support copy",
            "dvmconsole-support-sanitized.yml",
            sanitized: true);
    }

    private async Task PickAndWriteExportAsync(string title, string suggestedName, bool sanitized)
    {
        if (CodeplugSavePathOverride is { } pickPath)
        {
            string? path = await pickPath(title, suggestedName);
            if (path is null)
                return;
            var filesystemDestination = new DesktopConfigurationDocumentSet(path);
            await WriteExportAsync(
                filesystemDestination.Primary.DisplayName,
                filesystemDestination,
                sanitized);
            return;
        }

        IStorageFile? file = await PickCodeplugSaveFileAsync(title, suggestedName);
        if (file is null)
            return;
        string displayName = await AvaloniaStorageThreading.Invoke(() => file.Name);
        using AvaloniaStorageConfigurationDocumentSet destination = await AvaloniaStorageThreading.Invoke(
            () => new AvaloniaStorageConfigurationDocumentSet(file));
        await WriteExportAsync(displayName, destination, sanitized);
    }

    private async Task WriteExportAsync(
        string displayName,
        IExportDocumentSet destination,
        bool sanitized)
    {
        viewModel.CommitPendingEdits();
        try
        {
            var source = new ConfigurationStudioExportDocumentSet(
                viewModel.FullExportText,
                viewModel.Document.SourcePath is { } sourcePath
                    ? Path.GetFileName(sourcePath)
                    : "codeplug.yml",
                viewModel.CaptureExportCompanionContents());
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
        if (sessionController.ManagedConfigurationId is ConfigurationId id)
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
        try
        {
            if (!allowClose)
                viewModel.CommitPendingEdits();
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
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Configuration Studio close", exception);
            await ShowMessageAsync(
                "Unable to close Configuration Studio",
                $"The draft could not be saved or discarded. Configuration Studio will remain open.\n\n{exception.Message}");
        }
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
