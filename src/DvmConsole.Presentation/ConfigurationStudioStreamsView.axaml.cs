using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioStreamsView : UserControl
{
    private const double NarrowWidth = 760;
    private int queuedZoneChangeVersion;

    public ConfigurationStudioStreamsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        Unloaded += (_, _) => queuedZoneChangeVersion++;
    }

    public event EventHandler? DeleteRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleAddStreamClick(object? sender, RoutedEventArgs e) => ViewModel?.AddStream();
    private void HandleDeleteStreamClick(object? sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ViewModel?.CommitFieldEdit();
    }

    private void HandleStreamZoneChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedItem: ZoneConfiguration zone })
            return;

        int version = ++queuedZoneChangeVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsLoaded && version == queuedZoneChangeVersion)
                ViewModel?.MoveSelectedStreamTo(zone);
        }, DispatcherPriority.Background);
    }

    private void ApplyResponsiveLayout(double width)
    {
        Grid? body = this.FindControl<Grid>("StreamsBody");
        Border? inspector = this.FindControl<Border>("StreamInspector");
        if (body is null || inspector is null)
            return;

        bool narrow = width > 0 && width < NarrowWidth;
        body.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "3*,2*");
        body.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
        Grid.SetColumn(inspector, narrow ? 0 : 1);
        Grid.SetRow(inspector, narrow ? 1 : 0);
        inspector.Margin = narrow ? new Avalonia.Thickness(0, 10, 0, 0) : default;
    }
}
