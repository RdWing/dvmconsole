// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Host navigation for managed configuration import and inspection.</summary>
public sealed class MobileConfigurationView(
    Func<IConfigurationLibrary> createLibrary,
    Func<IConfigurationExportArchive>? createExportArchive = null,
    Func<IConfigurationLibrary, ConfigurationReference, CancellationToken, Task>? openConsole = null,
    Func<IConfigurationLibrary, ConfigurationReference, CancellationToken, ValueTask<MobileStudioSession>>? createStudio = null,
    Func<IConfigurationLibrary, ConfigurationDraft, CancellationToken, ValueTask<MobileStudioSession>>? createDraftStudio = null,
    Func<MobileSession>? currentSession = null,
    ConsoleHostFormFactor formFactor = ConsoleHostFormFactor.Phone) : UserControl
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl page = new();
    private readonly ContentControl prompt = new();
    private readonly StackPanel entries = new() { Spacing = 12 };
    private IConfigurationLibrary? library;
    private Control? libraryContent;
    private bool busy;
    private bool initialized;
    private TaskCompletionSource<string>? pendingChoice;
    internal Task Initialization { get; private set; } = Task.CompletedTask;

    public MobileConfigurationView(IConfigurationLibrary library) : this(() => library) { }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        if (createDraftStudio is not null && createExportArchive is not null)
            toolbar.Children.Add(ActionButton("New configuration", CreateNewAsync));
        toolbar.Children.Add(ActionButton("Import from Files", ImportAsync));
        toolbar.Children.Add(ActionButton("Refresh", RefreshAsync));
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "Configuration Library", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold }.WithScaledFontSize(24));
        body.Children.Add(new TextBlock
        {
            Text = openConsole is null
                ? "Imported files are copied into this app. Radio connections are not enabled in this build."
                : "Save a bundle to iCloud Drive in Files, then import it on another device. Imports are local copies; open a saved configuration to activate it.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(toolbar);
        if (createDraftStudio is not null && createExportArchive is not null)
            body.Children.Add(new TextBlock
            {
                Text = "No existing files? Start a new configuration in Studio. Add encryption keys under Encryption keys, " +
                    "and RID aliases under Files & Interop. Studio creates their companion files when you save.",
                TextWrapping = TextWrapping.Wrap
            });
        body.Children.Add(status);
        body.Children.Add(prompt);
        body.Children.Add(page);
        page.Content = entries;
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        libraryContent = (Control)Content;
        this.Bind(BackgroundProperty, this.GetResourceObservable("ShellBackgroundBrush"));
        Unloaded += (_, _) => pendingChoice?.TrySetResult("Cancel");
        Loaded += async (_, _) =>
        {
            if (initialized)
                return;
            initialized = true;
            Initialization = ExecuteAsync(RefreshAsync);
            await Initialization;
        };
    }

    /// <summary>Reopens the pinned active revision without advancing it to a later save.</summary>
    public async Task<bool> OpenActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (openConsole is null || Library is not IActiveConfigurationService { Active: { } active })
            return false;
        await openConsole(Library, active, cancellationToken);
        return true;
    }

    internal bool SupportsStudio => createStudio is not null && createExportArchive is not null;

    private Action? studioSettingsReturn;
    internal Task OpenActiveStudioAsync(Action? returnToSettings = null)
        => ExecuteAsync(async () =>
        {
            // Returning to Settings must not discard an editor already open here.
            if (Content is MobileConfigurationStudioView) return;
            studioSettingsReturn = returnToSettings;
            if (Library is IActiveConfigurationService { Active: { } active })
            {
                await foreach (ConfigurationSummary item in Library.ListAsync())
                {
                    if (item.Id != active.Id) continue;
                    await OpenStudioAsync(item);
                    return;
                }
            }
            await RefreshAsync();
            status.Text = "Start a new configuration, or choose Edit in Studio on a saved configuration.";
        });

    private IConfigurationLibrary Library => library ??= createLibrary();

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = 44, Margin = new Thickness(0, 0, 8, 8) };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await ExecuteAsync(action);
        return button;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy)
            return;
        busy = true;
        try { await action(); }
        catch (OperationCanceledException) { status.Text = "Cancelled."; }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { busy = false; }
    }

    private async Task RefreshAsync()
    {
        var configurations = new List<ConfigurationSummary>();
        var trash = new List<ConfigurationSummary>();
        await foreach (ConfigurationSummary item in Library.ListAsync()) configurations.Add(item);
        await foreach (ConfigurationSummary item in Library.ListTrashAsync()) trash.Add(item);
        entries.Children.Clear();
        page.Content = entries;
        foreach (ConfigurationSummary item in configurations)
            entries.Children.Add(CreateEntry(item, trashed: false));
        if (trash.Count > 0)
            entries.Children.Add(new TextBlock { Text = "Trash" }.WithScaledFontSize(20));
        foreach (ConfigurationSummary item in trash)
            entries.Children.Add(CreateEntry(item, trashed: true));
        status.Text = configurations.Count == 0
            ? "No saved configurations. Start a new configuration or import a YAML file."
            : $"{configurations.Count} saved configuration(s).";
    }

    private Control CreateEntry(ConfigurationSummary item, bool trashed)
    {
        Button EntryAction(string label, Func<Task> action)
        {
            var button = ActionButton(label, action);
            AutomationProperties.SetName(button, $"{label}: {item.Name}");
            return button;
        }

        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }.WithScaledFontSize(18));
        row.Children.Add(new TextBlock { Text = $"Revision {item.CurrentRevision.Value:N}"[..25], TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        if (trashed)
            actions.Children.Add(EntryAction("Restore", async () => { await Library.RestoreFromTrashAsync(item.Id); await RefreshAsync(); }));
        else
        {
            if (openConsole is not null)
                actions.Children.Add(EntryAction("Open console", async () =>
                {
                    await openConsole(Library, new(item.Id, item.CurrentRevision), CancellationToken.None);
                    await RefreshAsync();
                }));
            if (createStudio is not null && createExportArchive is not null)
                actions.Children.Add(EntryAction("Edit in Studio", () => OpenStudioAsync(item)));
            actions.Children.Add(EntryAction("Inspect YAML", () => InspectAsync(item)));
            actions.Children.Add(EntryAction("Export sanitized YAML", () => ExportAsync(item)));
            if (createExportArchive is not null)
                actions.Children.Add(EntryAction("Save bundle to Files…", () => ExportBundleAsync(item)));
            actions.Children.Add(EntryAction("Duplicate", async () => { await Library.DuplicateAsync(item.Id, item.Name + " copy"); await RefreshAsync(); }));
            if (!item.IsActive)
                actions.Children.Add(EntryAction("Move to Trash", async () => { await Library.MoveToTrashAsync(item.Id); await RefreshAsync(); }));
        }
        row.Children.Add(actions);
        var border = new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("ControlBorderBrush"));
        return border;
    }

    private async Task CreateNewAsync()
    {
        ConfigurationDraft draft;
        try { draft = await Library.CreateDraftAsync("Untitled Configuration"); }
        catch (ConfigurationDraftConflictException conflict)
        {
            string choice = await ChooseAsync("An unfinished Studio draft already exists. Keep it by continuing that draft, or discard it to start fresh.",
                "Continue draft", "Discard and start new", "Cancel");
            if (choice == "Cancel") return;
            if (choice == "Continue draft") draft = conflict.ExistingDraft;
            else
            {
                await Library.DiscardDraftAsync(conflict.ExistingDraft.Id);
                draft = await Library.CreateDraftAsync("Untitled Configuration");
            }
        }
        MobileStudioSession studio = await createDraftStudio!(Library, draft, CancellationToken.None);
        await ShowStudioAsync(studio, draft.Id);
    }

    private Task OpenStudioAsync(ConfigurationSummary item)
        => OpenStudioAsync(new ConfigurationReference(item.Id, item.CurrentRevision));

    private async Task OpenStudioAsync(ConfigurationReference reference)
    {
        MobileStudioSession studio = await createStudio!(Library, reference, CancellationToken.None);
        await ShowStudioAsync(studio, reference.Id);
    }

    private async Task ShowStudioAsync(MobileStudioSession studio, ConfigurationId id)
    {
        try
        {
            var returnToSettings = studioSettingsReturn;
            studioSettingsReturn = null;
            Content = new MobileConfigurationStudioView(studio, id,
                async reference =>
                {
                    if (openConsole is null) return false;
                    await openConsole(Library, reference, CancellationToken.None);
                    return true;
                },
                async () => { Content = libraryContent; await RefreshAsync(); returnToSettings?.Invoke(); },
                createExportArchive!, currentSession, formFactor, returnToSettings is null ? "Library" : "Settings");
        }
        catch { await studio.DisposeAsync(); throw; }
    }

    private async Task InspectAsync(ConfigurationSummary item)
    {
        using var export = new BufferedYamlExport();
        await Library.ExportAsync(new ConfigurationReference(item.Id, item.CurrentRevision), export,
            new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: false));
        await using Stream input = await export.OpenReadAsync();
        using var reader = new StreamReader(input);
        string yaml = await reader.ReadToEndAsync();
        // Inspect the saved revision without opening or changing a Studio draft.
        var detail = new StackPanel { Spacing = 12 };
        detail.Children.Add(ActionButton("Back to Library", RefreshAsync));
        detail.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap }.WithScaledFontSize(20));
        detail.Children.Add(new TextBlock { Text = "Read-only inspection of the saved revision. Use Edit in Studio to make changes.", TextWrapping = TextWrapping.Wrap });
        detail.Children.Add(new TextBox
        {
            Text = yaml,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 240,
            FontFamily = new FontFamily("monospace")
        });
        page.Content = detail;
        status.Text = "Saved revision · YAML export view";
    }

    private async Task ExportAsync(ConfigurationSummary item)
    {
        using var export = new BufferedYamlExport();
        await Library.ExportAsync(new ConfigurationReference(item.Id, item.CurrentRevision), export,
            new ConfigurationExportOptions(Sanitized: true, IncludeCompanions: false));
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider ??
            throw new InvalidOperationException("Document access is not available.");
        using IStorageFile? destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export sanitized configuration",
            SuggestedFileName = "configuration-sanitized.yaml",
            DefaultExtension = "yaml",
            FileTypeChoices = [new FilePickerFileType("YAML") { Patterns = ["*.yaml"] }]
        });
        if (destination is null) return;
        await using Stream input = await export.OpenReadAsync();
        await using Stream output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await input.CopyToAsync(output);
        await output.FlushAsync();
        status.Text = "Sanitized YAML exported without companion files.";
    }

    private async Task ExportBundleAsync(ConfigurationSummary item)
    {
        string choice = await ChooseAsync(
            "Save this revision to Files, including iCloud Drive. The full bundle includes aliases, companion files, passwords and encryption keys. Include these secrets only in storage you trust. For sharing without secrets, use Export sanitized YAML.",
            "Export full bundle", "Cancel");
        if (choice != "Export full bundle") return;
        await using IConfigurationExportArchive export = createExportArchive!();
        await Library.ExportAsync(new ConfigurationReference(item.Id, item.CurrentRevision), export,
            new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true));
        IReadableDocument archive = await export.CreateArchiveAsync();
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider ??
            throw new InvalidOperationException("Document access is not available.");
        using IStorageFile? destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save bundle to Files",
            SuggestedFileName = $"configuration-{item.Id}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("ZIP archive")
            { Patterns = ["*.zip"], AppleUniformTypeIdentifiers = ["public.zip-archive"] }]
        });
        if (destination is null) return;
        await using Stream input = await archive.OpenReadAsync();
        await using Stream output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await input.CopyToAsync(output);
        await output.FlushAsync();
        status.Text = "Bundle saved to Files. On another device, choose Import from Files and select this ZIP. iCloud Drive transfers the file; activation stays manual.";
    }

    private async Task ImportAsync()
    {
        IStorageProvider storage = TopLevel.GetTopLevel(this)?.StorageProvider ??
            throw new InvalidOperationException("Document access is not available.");
        IReadOnlyList<IStorageFile> primary = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import configuration from Files",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("YAML") { Patterns = ["*.yml", "*.yaml", "*.zip"], AppleUniformTypeIdentifiers = ["public.yaml", "public.zip-archive", "public.data"] }]
        });
        if (primary.Count == 0)
            return;
        IReadOnlyList<IStorageFile> companions = [];
        ConfigurationBundleImport? bundle = null;
        try
        {
            bool isBundle = primary[0].Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string choice = isBundle ? "Bundle" : await ChooseAsync("Does this configuration use companion files, such as encryption keys or subscriber aliases?", "Select companion files", "YAML only", "Cancel");
            if (choice == "Cancel")
                return;
            if (choice == "Select companion files")
            {
                companions = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select this configuration's companion files",
                    AllowMultiple = true
                });
                if (companions.Count == 0)
                    return;
            }
            if (isBundle) bundle = await ConfigurationBundleImport.ReadAsync(new StorageFileDocument(primary[0]));
            IImportDocumentSet documents = bundle is not null ? bundle : new SelectedImportDocuments(new StorageFileDocument(primary[0]),
                companions.Select(file => (IReadableDocument)new StorageFileDocument(file)).ToArray());
            var options = new ConfigurationImportOptions();
            while (true)
            {
                try
                {
                    ConfigurationImportResult result = await Library.ImportAsync(documents, options);
                    await RefreshAsync();
                    status.Text = (result.ReusedExisting ? "This configuration is already saved." : "Configuration saved in the app.") +
                        (result.Warnings.Count == 0 ? string.Empty : "\n" + string.Join("\n", result.Warnings));
                    return;
                }
                catch (ConfigurationExternalCompanionsConfirmationRequiredException exception)
                {
                    string answer = await ChooseAsync("This YAML refers to external files:\n" + string.Join("\n", exception.References) +
                        "\nOnly the files you selected will be copied into the app.", "Confirm selected companions", "Cancel");
                    if (answer == "Cancel") return;
                    options = options with { ConfirmExternalCompanions = true };
                }
                catch (ConfigurationImportConflictException exception)
                {
                    string answer = await ChooseAsync("This import conflicts with a saved configuration. Replacing it creates a new managed revision.",
                        "Import as new", "Replace existing", "Cancel");
                    if (answer == "Cancel") return;
                    options = options with
                    {
                        ConflictResolution = answer == "Import as new" ? ConfigurationConflictResolution.ImportAsNew : ConfigurationConflictResolution.ReplaceExisting,
                        ReplaceConfigurationId = answer == "Replace existing" ? exception.ExistingConfigurationId : null
                    };
                }
            }
        }
        finally
        {
            prompt.Content = null;
            bundle?.Dispose();
            foreach (IStorageFile file in companions) file.Dispose();
            foreach (IStorageFile file in primary) file.Dispose();
        }
    }

    private async Task<string> ChooseAsync(string message, params string[] choices)
    {
        if (!IsLoaded) return "Cancel";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingChoice = completion;
        var panel = new StackPanel { Spacing = 8 };
        var explanation = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(explanation, message);
        panel.Children.Add(explanation);
        foreach (string choice in choices)
        {
            // These buttons settle the pending operation, so they bypass its busy gate.
            var button = new Button { Content = choice, MinHeight = 44 };
            button.Click += (_, _) => completion.TrySetResult(choice);
            panel.Children.Add(button);
        }
        prompt.Content = panel;
        string selected = await completion.Task;
        pendingChoice = null;
        prompt.Content = null;
        return selected;
    }
}
