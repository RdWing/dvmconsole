using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DvmConsole.Desktop;

internal sealed partial class ChannelCardsRenderer : UserControl
{
    public ChannelCardsRenderer()
    {
        InitializeComponent();
    }

    public event EventHandler<PointerPressedEventArgs>? ChannelPointerPressed;
    public event EventHandler<PointerEventArgs>? ChannelPointerMoved;
    public event EventHandler<PointerReleasedEventArgs>? ChannelPointerReleased;
    public event EventHandler<PointerCaptureLostEventArgs>? ChannelPointerCaptureLost;
    public event EventHandler<RoutedEventArgs>? TransmitSelectionClick;
    public event EventHandler<RoutedEventArgs>? PageSelectionClick;
    public event EventHandler<RoutedEventArgs>? AlertSelectionClick;

    private void HandleChannelPointerPressed(object? sender, PointerPressedEventArgs e)
        => ChannelPointerPressed?.Invoke(sender, e);

    private void HandleChannelPointerMoved(object? sender, PointerEventArgs e)
        => ChannelPointerMoved?.Invoke(sender, e);

    private void HandleChannelPointerReleased(object? sender, PointerReleasedEventArgs e)
        => ChannelPointerReleased?.Invoke(sender, e);

    private void HandleChannelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => ChannelPointerCaptureLost?.Invoke(sender, e);

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
        => TransmitSelectionClick?.Invoke(sender, e);

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
        => PageSelectionClick?.Invoke(sender, e);

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
        => AlertSelectionClick?.Invoke(sender, e);
}
