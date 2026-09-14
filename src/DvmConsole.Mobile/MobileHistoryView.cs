// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Host-owned navigation and document export around the shared History presentation.</summary>
public sealed class MobileHistoryView : UserControl
{
    private readonly Func<MobileSession> session;
    private readonly UnifiedCallHistoryViewModel model = new();
    private readonly CallHistoryView history;
    private readonly Button reload = new() { Content = "↻", MinHeight = 44, MinWidth = 44 };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly DispatcherTimer refresh;
    private IConsoleApplicationSession? displayedSession;
    private bool busy;
    private Action? cancelConfirmation;
    private IReadOnlyList<RecordingArchiveEntry> catalog = [];
    private long loadedCatalogRevision = -1;
    public event EventHandler? SettingsRequested;

    public MobileHistoryView(Func<MobileSession> session)
    {
        this.session = session;
        status.IsVisible = false;
        status.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty)
                status.IsVisible = !string.IsNullOrWhiteSpace(status.Text);
        };
        history = new CallHistoryView { DataContext = model };
        history.UseTouchLayout();
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        AutomationProperties.SetName(back, "Back to Settings");
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12), RowSpacing = 8 };
        var navigation = MobileSettingsPageLayout.Heading("History", back);
        navigation.MinHeight = 44;
        navigation.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(reload, 2);
        navigation.Children.Add(reload);
        AutomationProperties.SetName(reload, "Refresh recordings");
        body.Children.Add(navigation);
        Grid.SetRow(status, 1);
        body.Children.Add(status);
        Grid.SetRow(history, 2);
        body.Children.Add(history);
        Content = body;
        history.ClearFiltersRequested += (_, _) => model.ClearHistoryFilters();
        history.ClearRequested += async (_, _) => await RunAsync(async current =>
        {
            if (current.Application.Commands is IConsoleHistoryCommands commands)
                await commands.ClearSessionHistoryAsync();
            status.Text = "Session history cleared. TAR recordings are retained.";
        });
        history.ExportRequested += async (_, _) => await RunAsync(ExportAsync);
        reload.Click += async (_, _) => await RunAsync(LoadArchiveAsync);
        history.PlaybackToggleRequested += async (_, args) => await RunAsync(async current =>
        {
            if (RecordingFor(args.Item) is not { } recording || current.Application.Commands is not IConsoleRecordingPlaybackCommands commands) return;
            if (commands.IsRecordingPlaying(recording.Id)) await commands.StopRecordingPlaybackAsync();
            else await PlayRecordingAsync(current, commands, recording.Id);
        });
        history.OpenRequested += async (_, args) =>
        {
            if (RecordingFor(args.Item) is { } recording)
                await RunAsync(current => ExportRecordingAsync(current, recording));
        };
        RecordingId? pendingRecording = null;
        IConsoleApplicationSession? pendingSession = null;
        history.DeleteRequested += async (_, args) =>
        {
            if (busy || RecordingFor(args.Item) is not { } recording) return;
            MobileSession requestedSession = session();
            if (requestedSession.RecordingArchive is not { } archive) return;
            bool confirmed = pendingRecording == recording.Id && ReferenceEquals(pendingSession, requestedSession.Application);
            cancelConfirmation?.Invoke();
            if (!confirmed)
            {
                Button? button = history.GetVisualDescendants().OfType<Button>()
                    .SingleOrDefault(candidate => ReferenceEquals(candidate.Tag, args.Item) && Equals(candidate.Content, "Delete"));
                if (button is null) return;
                pendingRecording = recording.Id;
                pendingSession = requestedSession.Application;
                button.Content = "Confirm?";
                AutomationProperties.SetName(button, $"Confirm delete {recording.FileName}");
                void Cancel()
                {
                    pendingRecording = null;
                    pendingSession = null;
                    cancelConfirmation = null;
                    button.DetachedFromVisualTree -= Detached;
                    button.Content = "Delete";
                    AutomationProperties.SetName(button, "Delete call recording");
                }
                void Detached(object? sender, VisualTreeAttachmentEventArgs e) => Cancel();
                button.DetachedFromVisualTree += Detached;
                cancelConfirmation = Cancel;
                return;
            }
            await RunAsync(async current =>
            {
                if (!ReferenceEquals(requestedSession.Application, current.Application)) return;
                if (current.Application.Commands is IConsoleRecordingPlaybackCommands commands && commands.IsRecordingPlaying(recording.Id))
                    await commands.StopRecordingPlaybackAsync();
                if (!ReferenceEquals(requestedSession.Application, session().Application)) return;
                bool deleted = await archive.DeleteAsync(recording.Id);
                await LoadArchiveAsync(current);
                status.Text = deleted ? "Recording deleted." : "Recording could not be deleted.";
            });
        };
        refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refresh.Tick += async (_, _) =>
        {
            Refresh();
            if (session().RecordingArchive is { } archive && archive.Revision != loadedCatalogRevision)
                await RunAsync(LoadArchiveAsync);
        };
        AttachedToVisualTree += async (_, _) =>
        {
            Refresh(); refresh.Start();
            await RunAsync(LoadArchiveAsync);
        };
        DetachedFromVisualTree += (_, _) => { refresh.Stop(); cancelConfirmation?.Invoke(); };
    }

    internal async Task PlayRecordingAsync(MobileSession current, IConsoleRecordingPlaybackCommands commands, RecordingId id)
    {
        // Play is an explicit audio-resume request, including when no FNE is connected.
        if (current.Execution?.Snapshot.CanReceive == false && current.ResumeListening is { } resume)
            await resume(CancellationToken.None);
        if (!ReferenceEquals(current.Application, session().Application))
            throw new OperationCanceledException("The active configuration changed before recording playback started.");
        await commands.PlayRecordingAsync(id);
    }

    private void Refresh()
    {
        MobileSession current = session();
        if (!ReferenceEquals(displayedSession, current.Application))
        {
            cancelConfirmation?.Invoke();
            displayedSession = current.Application;
            model.ClearHistoryFilters();
            catalog = [];
            loadedCatalogRevision = -1;
            status.Text = string.Empty;
        }
        history.DataContext = model;
        reload.IsVisible = current.RecordingArchive is not null;
        reload.IsEnabled = !busy && current.RecordingArchive is not null;
        var records = current.Application.History;
        model.Refresh(records, catalog, id => current.Application.Commands is IConsoleRecordingPlaybackCommands commands && commands.IsRecordingPlaying(id));
        history.ExportButton.IsEnabled = !busy && records.Count > 0 && current.ExportHistory is not null;
        history.ClearButton.IsEnabled = !busy && records.Count > 0 && current.Application.Commands is IConsoleHistoryCommands;
    }

    private async Task RunAsync(Func<MobileSession, Task> action)
    {
        if (busy) return;
        busy = true;
        Refresh();
        try { await action(session()); }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { busy = false; Refresh(); }
    }

    private async Task LoadArchiveAsync(MobileSession current)
    {
        if (current.RecordingArchive is not { } archive)
        { status.Text = "Open a configuration to browse recordings."; return; }
        // Capture before loading: a finalization during the read triggers one
        // more refresh. A failed read waits for a new revision or explicit retry.
        loadedCatalogRevision = archive.Revision;
        var entries = await archive.LoadAsync();
        if (!ReferenceEquals(current.Application, session().Application)) return;
        catalog = entries;
        status.Text = string.Empty;
    }

    private static RecordingArchiveEntry? RecordingFor(object item) => item switch
    {
        RecordingHistoryItemViewModel archive => archive.Recording,
        ConsoleCallHistoryItemViewModel call => call.Recording,
        _ => null
    };

    private async Task ExportRecordingAsync(MobileSession current, RecordingArchiveEntry recording)
    {
        if (current.RecordingArchive is not { } archive) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave) { status.Text = "Document export is unavailable."; return; }
        using var destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export recording",
            SuggestedFileName = recording.FileName,
            DefaultExtension = "opus",
            FileTypeChoices = [new FilePickerFileType("Opus audio") { Patterns = ["*.opus", "*.ogg"] }]
        });
        if (destination is null) return;
        await using var output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await archive.ExportAsync(recording.Id, output);
        await output.FlushAsync();
        status.Text = "Recording exported.";
    }

    private async Task ExportAsync(MobileSession current)
    {
        if (current.ExportHistory is not { } export) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave) { status.Text = "Document export is unavailable."; return; }
        using IStorageFile? destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export session history",
            SuggestedFileName = "console-history.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV")
            { Patterns = ["*.csv"], AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"] }]
        });
        if (destination is null) return;
        await using Stream output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await export(output, CancellationToken.None);
        await output.FlushAsync();
        status.Text = "Session history exported.";
    }
}
