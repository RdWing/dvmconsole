using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class PttSettingsView : UserControl
{
    public PttSettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler? ApplyGlobalKeyRequested;
    public event EventHandler? ApplyActiveSystemKeyRequested;
    public event EventHandler? KeyboardPermissionRequested;
    public event EventHandler? RefreshSerialDevicesRequested;
    public event EventHandler? ApplySerialSettingsRequested;

    private void HandleApplyGlobalKeyClick(object? sender, RoutedEventArgs e)
        => ApplyGlobalKeyRequested?.Invoke(this, EventArgs.Empty);
    private void HandleApplyActiveSystemKeyClick(object? sender, RoutedEventArgs e)
        => ApplyActiveSystemKeyRequested?.Invoke(this, EventArgs.Empty);
    private void HandleRequestKeyboardPermissionClick(object? sender, RoutedEventArgs e)
        => KeyboardPermissionRequested?.Invoke(this, EventArgs.Empty);
    private void HandleRefreshSerialDevicesClick(object? sender, RoutedEventArgs e)
        => RefreshSerialDevicesRequested?.Invoke(this, EventArgs.Empty);
    private void HandleApplySerialSettingsClick(object? sender, RoutedEventArgs e)
        => ApplySerialSettingsRequested?.Invoke(this, EventArgs.Empty);
}
