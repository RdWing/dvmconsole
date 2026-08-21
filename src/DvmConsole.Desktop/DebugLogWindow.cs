using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DvmConsole.Core.Diagnostics;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

public sealed class DebugLogWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly INotifyCollectionChanged debugLogCollection;
    private readonly ItemsControl logs;
    private readonly ScrollViewer logScroller;
    private readonly ScrollViewportAnchor<DebugLogEntry> logViewportAnchor;

    public DebugLogWindow(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        debugLogCollection = (INotifyCollectionChanged)viewModel.DebugLogEntries;
        Title = "Debug Logs";
        Width = 920;
        Height = 580;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Bind(BackgroundProperty, new Binding(nameof(MainWindowViewModel.MainBackgroundBrush)));
        DataContext = viewModel;
        Bind(FontSizeProperty, new Binding(nameof(MainWindowViewModel.UiFontSize)));

        var filterInput = new TextBox
        {
            Watermark = "Filter source or message (all terms)",
            MinWidth = 260
        };
        filterInput.Bind(TextBox.TextProperty, new Binding(nameof(MainWindowViewModel.DebugLogFilterText))
        {
            Mode = BindingMode.TwoWay
        });

        var severityFilter = new ComboBox
        {
            ItemsSource = viewModel.DebugLogSeverityFilters,
            SelectedItem = viewModel.DebugLogSeverityFilter,
            MinWidth = 110
        };
        severityFilter.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MainWindowViewModel.DebugLogSeverityFilter))
        {
            Mode = BindingMode.TwoWay
        });

        logs = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<DebugLogEntry>(
                (entry, _) => new TextBlock
                {
                    Text = entry.Summary,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("monospace"),
                    Margin = new Thickness(0, 0, 0, 5)
                })
        };
        logs.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainWindowViewModel.FilteredDebugLogs)));
        logScroller = new ScrollViewer
        {
            Content = logs,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        logViewportAnchor = new ScrollViewportAnchor<DebugLogEntry>(
            () => logScroller,
            () => logs.GetVisualDescendants().OfType<TextBlock>(),
            control => control.DataContext as DebugLogEntry);

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
                    Child = logScroller
                },
                new TextBlock
                {
                    Text = "Network payloads and credential-like values are redacted before display and export.",
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        Grid.SetRow((Control)content.Children[2], 2);
        Grid.SetRow((Control)content.Children[3], 3);
        Grid.SetRow(controls, 1);
        Content = content;

        closeButton.Click += (_, _) => Close();
        clearTextButton.Click += (_, _) =>
        {
            viewModel.DebugLogFilterText = string.Empty;
            filterInput.Focus();
        };
        exportButton.Click += HandleExportClick;
        debugLogCollection.CollectionChanged += HandleDebugLogCollectionChanged;
        logs.LayoutUpdated += HandleLogsLayoutUpdated;
        Closed += HandleClosed;
    }

    private void HandleDebugLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
        debugLogCollection.CollectionChanged -= HandleDebugLogCollectionChanged;
        logViewportAnchor.Reset();
    }

    private async void HandleExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
            return;

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
        string? path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.ExportDebugLogs(path);
    }
}
