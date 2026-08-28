using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DvmConsole.Core.Diagnostics;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

public sealed class DebugLogWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly ListBox logs;
    private readonly TextBox filterInput;
    private readonly ComboBox severityFilter;
    private readonly TextBlock retentionText;
    private readonly ScrollViewportAnchor<DebugLogEntry> logViewportAnchor;

    public DebugLogWindow(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "Debug Logs";
        Width = 920;
        Height = 580;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = viewModel.MainBackgroundBrush;
        DataContext = viewModel;
        FontSize = viewModel.UiFontSize;

        filterInput = new TextBox
        {
            Watermark = "Filter source or message (all terms)",
            MinWidth = 260,
            Text = viewModel.DebugLogFilterText
        };

        severityFilter = new ComboBox
        {
            ItemsSource = viewModel.DebugLogSeverityFilters,
            SelectedItem = viewModel.DebugLogSeverityFilter,
            MinWidth = 110
        };

        logs = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<DebugLogEntry>(
                (entry, _) => CreateLogRow(entry))
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            logs,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        logs.Styles.Add(CreateCompactLogItemStyle());
        logs.ItemsSource = viewModel.FilteredDebugLogs;
        logViewportAnchor = new ScrollViewportAnchor<DebugLogEntry>(
            () => logs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(),
            () => logs.GetVisualDescendants().OfType<ListBoxItem>(),
            control => control is ListBoxItem item
                ? item.DataContext as DebugLogEntry ?? item.Content as DebugLogEntry
                : null);

        var closeButton = new Button { Content = "Close", MinWidth = 88 };
        var clearTextButton = new Button { Content = "Clear Text", MinWidth = 100 };
        var exportButton = new Button { Content = "Export redacted…", MinWidth = 140 };
        var controls = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,110,*"),
            ColumnSpacing = 8,
            RowSpacing = 8,
            Children =
            {
                new TextBlock { Text = "Severity", VerticalAlignment = VerticalAlignment.Center },
                severityFilter,
                filterInput,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { clearTextButton, exportButton, closeButton }
                }
            }
        };
        Grid.SetColumn(severityFilter, 1);
        Grid.SetColumn(filterInput, 2);
        Grid.SetColumnSpan(controls.Children[3], 3);
        Grid.SetRow(controls.Children[3], 1);

        retentionText = new TextBlock
        {
            Classes = { "muted" },
            Text = viewModel.DebugLogRetentionText
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "FNE and console diagnostics",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                controls,
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#111820")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#293847")),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10),
                    Child = logs
                },
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Network payloads and credential-like values are redacted before display and export.",
                            Classes = { "muted" },
                            TextWrapping = TextWrapping.Wrap
                        },
                        retentionText
                    }
                }
            }
        };
        Grid.SetRow((Control)content.Children[2], 2);
        Grid.SetRow((Control)content.Children[3], 3);
        Grid.SetRow(controls, 1);
        Content = content;

        closeButton.Click += (_, _) => Close();
        filterInput.TextChanged += HandleFilterTextChanged;
        severityFilter.SelectionChanged += HandleSeveritySelectionChanged;
        clearTextButton.Click += (_, _) =>
        {
            viewModel.DebugLogFilterText = string.Empty;
            filterInput.Focus();
        };
        exportButton.Click += HandleExportClick;
        viewModel.DebugLogCollectionChanging += HandleDebugLogCollectionChanging;
        viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        logs.LayoutUpdated += HandleLogsLayoutUpdated;
        Closed += HandleClosed;
    }

    internal static TextBlock CreateLogRow(DebugLogEntry? entry)
        => new()
        {
            // Virtualized item containers are briefly cleared with null content
            // while Avalonia recycles them.
            Text = entry?.Summary ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new FontFamily("monospace"),
            Margin = new Thickness(0)
        };

    internal static Style CreateCompactLogItemStyle()
    {
        var style = new Style(selector => selector.OfType<ListBoxItem>());
        style.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private void HandleDebugLogCollectionChanging(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            logViewportAnchor.Reset();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
            logViewportAnchor.Capture();
    }

    private void HandleLogsLayoutUpdated(object? sender, EventArgs e)
        => logViewportAnchor.Restore();

    private void HandleClosed(object? sender, EventArgs e)
    {
        Closed -= HandleClosed;
        logs.LayoutUpdated -= HandleLogsLayoutUpdated;
        viewModel.DebugLogCollectionChanging -= HandleDebugLogCollectionChanging;
        viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        filterInput.TextChanged -= HandleFilterTextChanged;
        severityFilter.SelectionChanged -= HandleSeveritySelectionChanged;
        logViewportAnchor.Reset();
    }

    private void HandleFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        string value = filterInput.Text ?? string.Empty;
        if (!value.Equals(viewModel.DebugLogFilterText, StringComparison.Ordinal))
            viewModel.DebugLogFilterText = value;
    }

    private void HandleSeveritySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (severityFilter.SelectedItem is string value &&
            !value.Equals(viewModel.DebugLogSeverityFilter, StringComparison.Ordinal))
        {
            viewModel.DebugLogSeverityFilter = value;
        }
    }

    private void HandleViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.MainBackgroundBrush):
                Background = viewModel.MainBackgroundBrush;
                break;
            case nameof(MainWindowViewModel.UiFontSize):
                FontSize = viewModel.UiFontSize;
                break;
            case nameof(MainWindowViewModel.DebugLogFilterText):
                filterInput.Text = viewModel.DebugLogFilterText;
                break;
            case nameof(MainWindowViewModel.DebugLogSeverityFilter):
                severityFilter.SelectedItem = viewModel.DebugLogSeverityFilter;
                break;
            case nameof(MainWindowViewModel.DebugLogRetentionText):
                retentionText.Text = viewModel.DebugLogRetentionText;
                break;
        }
    }

    private async void HandleExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            viewModel.ReportDebugLogExportFailure("This platform did not provide an available save picker.");
            return;
        }

        try
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Redacted Debug Logs",
                SuggestedFileName = "dvmconsole-debug.log",
                DefaultExtension = "log",
                FileTypeChoices =
                [
                    new FilePickerFileType("Redacted log")
                    {
                        Patterns = ["*.log"],
                        MimeTypes = ["text/plain"]
                    }
                ]
            });
            if (file is null)
                return;

            await using Stream destination = await file.OpenWriteAsync();
            viewModel.ExportDebugLogs(destination, file.Name);
        }
        catch (Exception exception)
        {
            viewModel.ReportDebugLogExportFailure(exception.Message);
        }
    }
}
