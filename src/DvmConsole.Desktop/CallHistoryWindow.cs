using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

/// <summary>
/// Modeless event-history view. The main shell keeps a compact docked view;
/// this window provides the legacy console's detachable, filterable view.
/// </summary>
public sealed class CallHistoryWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private bool snapToWindow;

    public CallHistoryWindow(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        WindowPlacementSetting placement = viewModel.GetCallHistoryWindowPlacement();

        Title = "Event History";
        Width = placement.Width;
        Height = placement.Height;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DataContext = viewModel;

        if (placement.Left is double left && placement.Top is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
        }

        var filterInput = new TextBox
        {
            Watermark = "Filter system, channel, RID, protocol",
            MinWidth = 240
        };
        filterInput.Bind(TextBox.TextProperty, new Binding(nameof(MainWindowViewModel.CallHistoryFilterText))
        {
            Mode = BindingMode.TwoWay
        });

        var history = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<CallHistoryEntry>(
                (entry, _) =>
                {
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("110,*,120,100,100"),
                        ColumnSpacing = 8
                    };
                    var timestamp = new TextBlock { Text = entry.TimestampText };
                    var channel = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = entry.DisplayChannelText, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = entry.RouteText, FontSize = 11, Opacity = 0.72 }
                        }
                    };
                    var system = new TextBlock { Text = entry.SystemName };
                    var encryption = new TextBlock { Text = entry.EncryptionText };
                    var duration = new TextBlock { Text = entry.DurationText };
                    row.Children.Add(timestamp);
                    row.Children.Add(channel);
                    row.Children.Add(system);
                    row.Children.Add(encryption);
                    row.Children.Add(duration);
                    Grid.SetColumn(channel, 1);
                    Grid.SetColumn(system, 2);
                    Grid.SetColumn(encryption, 3);
                    Grid.SetColumn(duration, 4);
                    return new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.Parse("#3A4654")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 8),
                        Child = row
                    };
                })
        };
        history.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainWindowViewModel.FilteredCallHistory)));

        var closeButton = new Button { Content = "Close", MinWidth = 88 };
        var clearButton = new Button { Content = "Clear", MinWidth = 88 };
        var exportButton = new Button { Content = "Export CSV…", MinWidth = 108 };
        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                new TextBlock { Text = "Event history", VerticalAlignment = VerticalAlignment.Center },
                filterInput,
                clearButton,
                exportButton,
                closeButton
            }
        };
        Grid.SetColumn(filterInput, 1);
        Grid.SetColumn(clearButton, 2);
        Grid.SetColumn(exportButton, 3);
        Grid.SetColumn(closeButton, 4);

        var historyBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#293847")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = new ScrollViewer { Content = history }
        };
        var footer = new TextBlock
        {
            Text = "This window is detached from the activity sidebar. Its placement is restored on the next launch.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16),
            RowSpacing = 10,
            Children =
            {
                controls,
                historyBorder,
                footer
            }
        };
        Grid.SetRow(historyBorder, 1);
        Grid.SetRow(footer, 2);
        Content = content;

        closeButton.Click += (_, _) => Close();
        clearButton.Click += (_, _) => viewModel.ClearCallHistory();
        exportButton.Click += HandleExportClick;
        Closed += (_, _) => SavePlacement();
    }

    public void SetSnapToWindow(bool enabled, MainWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        snapToWindow = enabled;
        if (!enabled)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            owner.Position.X + (int)Math.Round(owner.Bounds.Width) + 5,
            owner.Position.Y);
        Height = Math.Max(MinHeight, owner.Bounds.Height);
    }

    private async void HandleExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
            return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Event History",
            SuggestedFileName = "dvmconsole-call-history.csv",
            DefaultExtension = "csv",
            FileTypeChoices =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"],
                    AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"]
                }
            ]
        });
        string? path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.ExportCallHistory(path);
    }

    private void SavePlacement()
    {
        if (snapToWindow)
            return;
        viewModel.SaveCallHistoryWindowPlacement(new WindowPlacementSetting
        {
            Left = Position.X,
            Top = Position.Y,
            Width = Width,
            Height = Height
        });
    }
}
