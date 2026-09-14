// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.IO;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Touch navigation and document pickers around the shared Studio editor.</summary>
public sealed class MobileConfigurationStudioView : UserControl
{
    private readonly MobileStudioSession session;
    private readonly ConfigurationStudioView editor;
    private readonly ConfigurationStudioSaveController saves;
    private readonly ConfigurationStudioDocumentController edits;
    private readonly ContentControl prompt = new();
    private readonly Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly Border modal = new()
    {
        IsVisible = false,
        Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0))
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Func<ConfigurationReference, Task<bool>> reload;
    private readonly Func<Task> close;
    private readonly Func<IConfigurationExportArchive> createArchive;
    private TaskCompletionSource<bool>? pendingChoice;
    private bool busy;
    private readonly Func<MobileSession>? currentSession;

    public MobileConfigurationStudioView(MobileStudioSession session, ConfigurationId id,
        Func<ConfigurationReference, Task<bool>> reload, Func<Task> close,
        Func<IConfigurationExportArchive> createArchive, Func<MobileSession>? currentSession = null,
        ConsoleHostFormFactor formFactor = ConsoleHostFormFactor.Phone, string backDestination = "Library")
    {
        this.session = session;
        this.currentSession = currentSession;
        status.Text = session.InitialStatus;
        session.CheckpointFailed += message => status.Text = message;
        this.reload = reload;
        this.close = close;
        this.createArchive = createArchive;
        editor = new ConfigurationStudioView
        {
            DataContext = session.ViewModel,
            UseTouchLayout = true,
            PreferTouchSidebar = formFactor == ConsoleHostFormFactor.Tablet,
            ShowZoneLayoutPreview = formFactor != ConsoleHostFormFactor.Phone
        };
        editor.Styles.Add(new MobileStudioStyles());
        AttachedToVisualTree += (_, _) => RefreshStudioPalette();
        ActualThemeVariantChanged += (_, _) => RefreshStudioPalette();
        saves = new(session.ViewModel, id);
        edits = new(session.ViewModel);
        session.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ConfigurationStudioViewModel.IsGroups) && session.ViewModel.IsGroups)
                RefreshOperationalGroups();
        };
        RefreshOperationalGroups();
        var back = new Button { Content = "‹ " + backDestination, MinHeight = 44 };
        Avalonia.Automation.AutomationProperties.SetName(back, "Back to " + backDestination);
        back.Click += async (_, _) => await RunAsync(async () =>
        {
            session.ViewModel.CommitPendingEdits();
            if (session.ViewModel.IsDirty && !await ConfirmAsync("Discard draft?",
                $"Your edits have not been saved. Discard them and return to {backDestination}?", "Discard")) return;
            await session.DisposeAsync();
            await close();
        });
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0) };
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        navigation.Children.Add(back);
        var title = new TextBlock { Text = "Studio", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        navigation.Children.Add(title);
        var browse = new Button { Content = "Browse", MinHeight = 44 };
        Avalonia.Automation.AutomationProperties.SetName(browse, "Browse configuration");
        browse.Click += (_, _) => editor.ToggleTouchNavigation();
        Grid.SetColumn(browse, 2);
        navigation.Children.Add(browse);
        header.Children.Add(navigation);
        header.Children.Add(new ScrollViewer { Content = status, MaxHeight = 80 });

        layout.Children.Add(header);
        Grid.SetRow(editor, 1);
        layout.Children.Add(editor);
        var dialog = new Border
        {
            Child = prompt,
            Padding = new Thickness(20),
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        dialog.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBackgroundBrush"));
        modal.Child = dialog;
        KeyboardNavigation.SetTabNavigation(modal, KeyboardNavigationMode.Cycle);
        modal.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            pendingChoice?.TrySetResult(false);
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        Content = new Grid { Children = { layout, modal } };
        SizeChanged += (_, _) => UpdatePromptHeight();
        Unloaded += (_, _) => pendingChoice?.TrySetResult(false);
        editor.ReviewSaveRequested += async (_, _) => await SaveAsync(false);
        editor.SaveCopyRequested += async (_, _) => await SaveAsync(true);
        editor.EditCommandRequested += async (_, args) => await RunAsync(() =>
            edits.ExecuteAsync(args.Command, ConfirmAsync, editor.ZonesView.GetSelectedChannelRows()));
        editor.DeleteSystemRequested += async (_, _) => await RunAsync(() => edits.DeleteSelectedSystemAsync(ConfirmAsync));
        editor.DeleteStreamRequested += async (_, _) => await RunAsync(() => edits.DeleteSelectedStreamAsync(ConfirmAsync));
        editor.DeleteGroupRequested += async (_, _) => await RunAsync(() => edits.DeleteSelectedGroupAsync(ConfirmAsync));
        editor.DeleteKeyRequested += async (_, _) => await RunAsync(() => edits.DeleteSelectedKeyAsync(ConfirmAsync));
        editor.DeleteAliasRequested += async (_, _) => await RunAsync(() => edits.DeleteSelectedAliasAsync(ConfirmAsync));
        editor.BrowseKeyFileRequested += async (_, _) => await ImportCompanionAsync("Select encryption key file",
            (name, content) => session.ViewModel.AttachKeyFile(name, content));
        editor.BrowseAliasFileRequested += async (_, args) => await ImportCompanionAsync("Select RID alias file",
            (name, content) => session.ViewModel.AttachAliasFile(args.System, name, content));
        editor.ExportFullRequested += async (_, _) => await ExportAsync(false);
        editor.ExportSanitizedRequested += async (_, _) => await ExportAsync(true);
    }

    private void RefreshStudioPalette()
    {
        // Adapt shared Studio resources at the mobile host boundary. Desktop
        // keeps its palette; theme changes reuse the app's semantic brushes.
        foreach (var (studio, app) in new (string, string)[]
        {
            ("StudioTopBrush", "ShellBackgroundBrush"),
            ("StudioNavigationBrush", "ShellBackgroundBrush"),
            ("StudioCanvasBrush", "ShellBackgroundBrush"),
            ("StudioPanelBrush", "CardBackgroundBrush"),
            ("StudioInspectorBrush", "CardBackgroundBrush"),
            ("StudioFieldBrush", "ButtonBackgroundBrush"),
            ("StudioLineBrush", "ControlBorderBrush"),
            ("StudioMutedBrush", "MutedTextBrush"),
            ("StudioSelectionBrush", "OperationalSelectionSurfaceBrush"),
            ("StudioAccentBrush", "OperationalSelectionBorderBrush"),
            ("StudioPrimaryTextBrush", "PrimaryTextBrush")
        })
        {
            if (this.TryFindResource(app, ActualThemeVariant, out object? brush))
            {
                editor.Resources[studio] = brush;
                foreach (var theme in editor.Resources.ThemeDictionaries.Values.OfType<ResourceDictionary>())
                    theme.Remove(studio);
            }
        }
    }

    private Task SaveAsync(bool copy) => RunAsync(async () =>
    {
        await saves.ReviewAndSaveAsync(copy, offerReload: true, session.SaveServices,
            new(ConfirmAsync, (title, message) => { status.Text = title + "\n" + message; return Task.CompletedTask; }, reload));
        RefreshOperationalGroups();
    });

    private void RefreshOperationalGroups()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Active console groups",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        MobileSession? active = currentSession?.Invoke();
        bool MatchesActive() => active is not null &&
            ReferenceEquals(currentSession?.Invoke().Application, active.Application) &&
            active.Application.Topology.Configuration?.Id == saves.ManagedConfigurationId;
        if (MatchesActive() && active!.Application.Commands is IConsoleGroupSettings)
        {
            body.Children.Add(new TextBlock
            {
                Text = "These controls apply to the running console. Save and load definition changes before editing newly added groups here.",
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new MobileGroupView(active, embedded: true, isCurrent: MatchesActive));
        }
        else
            body.Children.Add(new TextBlock
            {
                Text = "Save and open this configuration in Console to edit group membership and enable patches. Draft definitions can be edited here without starting a session.",
                TextWrapping = TextWrapping.Wrap
            });
        editor.FindControl<ConfigurationStudioGroupsView>("GroupsPage")!.SetOperatorContent(body);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        var previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        editor.IsEnabled = false;
        try { await operation(); }
        catch (OperationCanceledException) { status.Text = "Cancelled."; }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { editor.IsEnabled = true; busy = false; if (IsLoaded) previousFocus?.Focus(); }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        if (!IsLoaded) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingChoice = completion;
        var confirmation = new MobileConfirmationPrompt(title, message, action,
            accepted => completion.TrySetResult(accepted));
        confirmation.Loaded += (_, _) => confirmation.FocusCancel();
        prompt.Content = confirmation;
        UpdatePromptHeight();
        layout.IsEnabled = false;
        modal.IsVisible = true;
        (ControlAutomationPeer.FromElement(this) as StudioAutomationPeer)?.RefreshChildren();
        try { return await completion.Task; }
        finally
        {
            pendingChoice = null;
            modal.IsVisible = false;
            (ControlAutomationPeer.FromElement(this) as StudioAutomationPeer)?.RefreshChildren();
            prompt.Content = null;
            layout.IsEnabled = true;
        }
    }

    private void UpdatePromptHeight()
    {
        if (prompt.Content is MobileConfirmationPrompt scroll)
            scroll.MaxHeight = Math.Max(0, Bounds.Height - 72);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StudioAutomationPeer(this);

    private sealed class StudioAutomationPeer(MobileConfigurationStudioView owner) : ControlAutomationPeer(owner)
    {
        public void RefreshChildren() => InvalidateChildren();

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
            => owner.modal.IsVisible && owner.prompt.Content is MobileConfirmationPrompt confirmation
                ? [CreatePeerForElement(confirmation)!]
                : base.GetChildrenCore() ?? [];
    }

    private IStorageProvider Storage => TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("Document access is unavailable.");

    private Task ImportCompanionAsync(string title, Func<string, string, string> attach) => RunAsync(async () =>
    {
        IReadOnlyList<IStorageFile> files = await Storage.OpenFilePickerAsync(new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("YAML companion")
            { Patterns = ["*.yml", "*.yaml", "*.clear"], AppleUniformTypeIdentifiers = ["public.text", "public.data"] }]
        });
        try
        {
            if (files.Count == 0) return;
            await using Stream input = await files[0].OpenReadAsync();
            byte[] bytes = await BoundedResourceReader.ReadBytesAsync(input,
                ManagedResourceLimits.ConfigurationCompanionBytes, "Studio companion");
            using var buffered = new MemoryStream(bytes, writable: false);
            string content = BoundedResourceReader.ReadUtf8(buffered,
                ManagedResourceLimits.ConfigurationCompanionBytes, "Studio companion");
            string name = attach(files[0].Name, content);
            status.Text = $"{name} added to this draft. Save to include it in the managed revision.";
        }
        finally { foreach (var file in files) file.Dispose(); }
    });

    private Task ExportAsync(bool sanitized) => RunAsync(async () =>
    {
        session.ViewModel.CommitPendingEdits();
        if (!sanitized && !await ConfirmAsync("Export full bundle",
            "The bundle includes companion files and may contain passwords and encryption keys.", "Export")) return;
        using var yaml = new BufferedYamlExport();
        await using IConfigurationExportArchive? archive = sanitized ? null : createArchive();
        IReadOnlyList<string> omitted = await session.ExportAsync(sanitized ? yaml : archive!, sanitized);
        IReadableDocument document = sanitized ? yaml : await archive!.CreateArchiveAsync();
        using IStorageFile? file = await Storage.SaveFilePickerAsync(new()
        {
            Title = sanitized ? "Export sanitized YAML" : "Export configuration bundle",
            SuggestedFileName = sanitized ? "configuration-sanitized.yaml" : "configuration.zip",
            DefaultExtension = sanitized ? "yaml" : "zip"
        });
        if (file is null) return;
        await using Stream input = await document.OpenReadAsync();
        await using Stream output = await file.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await input.CopyToAsync(output); await output.FlushAsync();
        status.Text = omitted.Count == 0 ? "Draft exported without saving a revision."
            : "Exported with missing companions:\n" + string.Join("\n", omitted);
    });
}
