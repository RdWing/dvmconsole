using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace DvmConsole.Presentation;

public sealed partial class CallHistoryView : UserControl
{
    private const double NarrowWidth = 600;

    public CallHistoryView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveRows();
        LayoutUpdated += (_, _) => ApplyResponsiveRows();
    }

    public event EventHandler? ExportRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? ClearFiltersRequested;
    public event EventHandler<CallHistoryItemEventArgs>? PlayRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<CallHistoryItemEventArgs>? OpenRequested;
    public event EventHandler<CallHistoryItemEventArgs>? DeleteRequested;

    public ListBox HistoryItems => HistoryList;

    private void HandleExportClick(object? sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);
    private void HandleClearClick(object? sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    private void HandleClearFiltersClick(object? sender, RoutedEventArgs e) => ClearFiltersRequested?.Invoke(this, EventArgs.Empty);
    private void HandleStopClick(object? sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);
    private void HandlePlayClick(object? sender, RoutedEventArgs e) => PublishItem(sender, PlayRequested);
    private void HandleOpenClick(object? sender, RoutedEventArgs e) => PublishItem(sender, OpenRequested);
    private void HandleDeleteClick(object? sender, RoutedEventArgs e) => PublishItem(sender, DeleteRequested);

    private void PublishItem(
        object? sender,
        EventHandler<CallHistoryItemEventArgs>? handler)
    {
        if (sender is Button { Tag: ICallHistoryItemViewModel item })
            handler?.Invoke(this, new CallHistoryItemEventArgs(item));
    }

    private void ApplyResponsiveRows()
    {
        bool narrow = Bounds.Width is > 0 and < NarrowWidth;
        foreach (Grid row in this.GetVisualDescendants()
                     .OfType<Grid>()
                     .Where(grid => grid.Classes.Contains("history-row-layout")))
        {
            bool rowIsNarrow = row.ColumnDefinitions.Count == 2;
            if (rowIsNarrow != narrow)
            {
                row.ColumnDefinitions = new ColumnDefinitions(narrow ? "80,*" : "80,*,Auto");
                row.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto");
            }

            WrapPanel? actions = row.GetVisualDescendants()
                .OfType<WrapPanel>()
                .FirstOrDefault(panel => panel.Classes.Contains("history-actions"));
            if (actions is null)
                continue;

            Grid.SetRow(actions, narrow ? 3 : 0);
            Grid.SetColumn(actions, narrow ? 0 : 2);
            Grid.SetRowSpan(actions, narrow ? 1 : 3);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            actions.Margin = narrow ? new Avalonia.Thickness(0, 3, 0, 0) : default;
        }
    }
}
