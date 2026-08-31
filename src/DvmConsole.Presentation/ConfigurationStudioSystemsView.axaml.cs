using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioSystemsView : UserControl
{
    private const double NarrowWidth = 760;

    public ConfigurationStudioSystemsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
    }

    public event EventHandler? DeleteRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleAddSystemClick(object? sender, RoutedEventArgs e) => ViewModel?.AddSystem();
    private void HandleDuplicateSystemClick(object? sender, RoutedEventArgs e) => ViewModel?.DuplicateSystem();
    private void HandleDeleteSystemClick(object? sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ViewModel?.CommitFieldEdit();
    }

    private void ApplyResponsiveLayout(double width)
    {
        Grid? body = this.FindControl<Grid>("SystemsBody");
        Border? list = this.FindControl<Border>("SystemsListPanel");
        Border? inspector = this.FindControl<Border>("SystemInspector");
        if (body is null || list is null || inspector is null)
            return;

        bool narrow = width > 0 && width < NarrowWidth;
        body.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "3*,2*");
        body.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(inspector, narrow ? 0 : 1);
        Grid.SetRow(inspector, narrow ? 1 : 0);
        inspector.Margin = narrow ? new Avalonia.Thickness(0, 10, 0, 0) : default;
    }
}
