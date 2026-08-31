using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ConnectionsSettingsView : UserControl
{
    public ConnectionsSettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler<ConnectionSystemEventArgs>? ToggleConnectionRequested;
    public event EventHandler<ConnectionSystemEventArgs>? RestartConnectionRequested;

    public bool TryBringKeyStatusIntoView()
    {
        ScrollViewer? scroller = this.FindControl<ScrollViewer>("ConnectionsScrollViewer");
        Control? anchor = this.FindControl<Control>("EncryptionKeyStatusSection");
        if (scroller is null || anchor is null || anchor.Bounds.Height <= 0)
            return false;

        Point? position = anchor.TranslatePoint(default, scroller);
        if (position is null)
            return false;

        double maximumOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        double desiredOffset = Math.Clamp(
            scroller.Offset.Y + position.Value.Y - 8,
            0,
            maximumOffset);
        scroller.Offset = new Vector(scroller.Offset.X, desiredOffset);
        return true;
    }

    private void HandleToggleConnectionClick(object? sender, RoutedEventArgs e)
        => Publish(sender, ToggleConnectionRequested);

    private void HandleRestartConnectionClick(object? sender, RoutedEventArgs e)
        => Publish(sender, RestartConnectionRequested);

    private void Publish(object? sender, EventHandler<ConnectionSystemEventArgs>? handler)
    {
        if (sender is Button { Tag: IConnectionSystemViewModel system })
            handler?.Invoke(this, new ConnectionSystemEventArgs(system));
    }
}
