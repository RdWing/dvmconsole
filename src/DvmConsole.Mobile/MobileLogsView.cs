// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using System.ComponentModel;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>Touch navigation and document export around the shared session log workspace.</summary>
public sealed class MobileLogsView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<MobileSession> session;
    private DebugLogWorkspace? workspace;
    private readonly CheckBox verbose = new() { Content = "Verbose radio logging", MinHeight = 44 };
    private bool refreshingVerbose;
    private bool savingVerbose;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };

    public MobileLogsView(Func<MobileSession> session)
    {
        this.session = session;
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(12) };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        AutomationProperties.SetName(back, "Back to Settings");
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var export = new Button { Content = "Export logs", MinHeight = 44, Margin = new Thickness(8, 0, 0, 0) };
        export.Click += async (_, _) =>
        {
            export.IsEnabled = false;
            try { await ExportAsync(); }
            catch (Exception exception) { status.Text = exception.Message; }
            finally { export.IsEnabled = true; }
        };
        root.Children.Add(MobileSettingsPageLayout.Heading("Logs", back));
        toolbar.Children.Add(export);
        toolbar.Children.Add(verbose);
        verbose.IsCheckedChanged += async (_, _) =>
        {
            if (refreshingVerbose || savingVerbose) return;
            var current = session().Application;
            if (current.Commands is not IConsoleDiagnosticSettings settings || !settings.CanSaveDiagnosticSettings) return;
            savingVerbose = true;
            verbose.IsEnabled = false;
            try { await settings.SetVerboseLoggingAsync(verbose.IsChecked == true); }
            catch (Exception exception)
            {
                if (ReferenceEquals(current, session().Application)) status.Text = exception.Message;
            }
            finally { savingVerbose = false; RefreshVerbose(); }
        };
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);
        var filters = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        var search = new TextBox { Watermark = "Search logs", MinHeight = 44 };
        AutomationProperties.SetName(search, "Search logs");
        search.TextChanged += (_, _) => { if (workspace is not null) workspace.FilterText = search.Text ?? string.Empty; };
        var severity = new ComboBox { MinHeight = 44, MinWidth = 100 };
        AutomationProperties.SetName(severity, "Minimum log severity");
        severity.SelectionChanged += (_, _) =>
        { if (workspace is not null && severity.SelectedItem is string selected) workspace.SeverityFilter = selected; };
        filters.Children.Add(search);
        Grid.SetColumn(severity, 1);
        filters.Children.Add(severity);
        Grid.SetRow(filters, 2);
        root.Children.Add(filters);
        var entries = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<DebugLogEntry>((entry, _) => new SelectableTextBlock
            {
                Text = entry?.Summary.TrimEnd('\r', '\n'),
                TextWrapping = TextWrapping.Wrap
            }.WithScaledFontSize(13))
        };

        entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = {
            new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 3)),
            new Setter(Layoutable.MinHeightProperty, 0d),
            new Setter(Layoutable.MarginProperty, new Thickness(0))
        }
        });
        entries.Bind(BackgroundProperty, this.GetResourceObservable("SidebarBackgroundBrush"));
        Grid.SetRow(entries, 3);
        root.Children.Add(entries);
        var footer = new StackPanel { Spacing = 4 };
        var retention = new TextBlock { TextWrapping = TextWrapping.Wrap }.WithScaledFontSize(12);
        void RefreshRetention(object? sender, PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(DebugLogWorkspace.RetentionText)) retention.Text = workspace?.RetentionText; }
        footer.Children.Add(retention);
        footer.Children.Add(status);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        Content = root;
        AttachedToVisualTree += (_, _) =>
        {
            RefreshVerbose();
            workspace = session().Diagnostics;
            entries.ItemsSource = workspace?.FilteredEntries;
            search.Text = workspace?.FilterText;
            severity.ItemsSource = workspace?.DebugLogSeverityFilters;
            severity.SelectedItem = workspace?.SeverityFilter;
            retention.Text = workspace?.RetentionText;
            if (workspace is not null) workspace.PropertyChanged += RefreshRetention;
            status.Text = workspace is null ? "No session diagnostics are available." : string.Empty;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (workspace is not null) workspace.PropertyChanged -= RefreshRetention;
            entries.ItemsSource = null;
            workspace = null;
        };
        this.Bind(BackgroundProperty, this.GetResourceObservable("ShellBackgroundBrush"));
    }

    private void RefreshVerbose()
    {
        refreshingVerbose = true;
        try
        {
            var settings = session().Application.Commands as IConsoleDiagnosticSettings;
            verbose.IsVisible = settings?.CanSaveDiagnosticSettings == true;
            verbose.IsEnabled = !savingVerbose;
            verbose.IsChecked = settings?.VerboseLoggingEnabled == true;
        }
        finally { refreshingVerbose = false; }
    }

    private async Task ExportAsync()
    {
        if (workspace is null) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            throw new InvalidOperationException("Document export is unavailable.");
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "Export diagnostic logs", SuggestedFileName = "console-logs.tsv", DefaultExtension = "tsv" });
        if (file is null) return;
        using (file)
        using (var buffered = new MemoryStream())
        {
            // Snapshot the UI-owned collection before asynchronous document access.
            int count = workspace.Export(buffered);
            buffered.Position = 0;
            await using Stream output = await file.OpenWriteAsync();
            if (output.CanSeek) { output.Position = 0; output.SetLength(0); }
            await buffered.CopyToAsync(output);
            status.Text = $"Exported {count:N0} sanitized log entries.";
        }
    }
}
