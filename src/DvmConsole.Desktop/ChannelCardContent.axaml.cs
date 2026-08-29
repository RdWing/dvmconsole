using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Desktop;

public sealed partial class ChannelCardContent : UserControl
{
    public static readonly StyledProperty<double> UiFontSizeProperty =
        AvaloniaProperty.Register<ChannelCardContent, double>(nameof(UiFontSize), 12);
    public static readonly StyledProperty<double> UiSmallFontSizeProperty =
        AvaloniaProperty.Register<ChannelCardContent, double>(nameof(UiSmallFontSize), 11);

    public ChannelCardContent()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? TransmitSelectionClick;
    public event EventHandler<RoutedEventArgs>? PageSelectionClick;
    public event EventHandler<RoutedEventArgs>? AlertSelectionClick;

    public double UiFontSize
    {
        get => GetValue(UiFontSizeProperty);
        set => SetValue(UiFontSizeProperty, value);
    }

    public double UiSmallFontSize
    {
        get => GetValue(UiSmallFontSizeProperty);
        set => SetValue(UiSmallFontSizeProperty, value);
    }

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
        => TransmitSelectionClick?.Invoke(sender, e);

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
        => PageSelectionClick?.Invoke(sender, e);

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
        => AlertSelectionClick?.Invoke(sender, e);

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
