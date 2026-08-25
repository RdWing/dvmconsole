using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Desktop;

internal readonly record struct ResponsiveToolbarVisibility(
    bool ShowClocks,
    bool ShowAlertToneShortcuts,
    bool ShowTonesLauncher,
    bool ShowOverflow);

internal static class MainWindowResponsiveToolbarPolicy
{
    // Microphone and output-state controls are deliberately absent from this
    // policy: operational controls never participate in toolbar overflow. Only
    // reference data and convenience shortcuts are shed.
    internal const double TonesLauncherMinimumWidth = 1_040;
    internal const double AlertToneShortcutsMinimumWidth = 1_340;
    internal const double ToolbarClocksMinimumWidth = 1_520;

    public static ResponsiveToolbarVisibility Evaluate(double availableWidth)
    {
        double width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        bool showClocks = width >= ToolbarClocksMinimumWidth;
        bool showAlertToneShortcuts = width >= AlertToneShortcutsMinimumWidth;
        bool showTonesLauncher = width >= TonesLauncherMinimumWidth;

        return new ResponsiveToolbarVisibility(
            showClocks,
            showAlertToneShortcuts,
            showTonesLauncher,
            ShowOverflow: !showClocks || !showAlertToneShortcuts || !showTonesLauncher);
    }
}

public sealed partial class MainWindow
{
    private void HandleResponsiveToolbarSizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyResponsiveToolbarVisibility(
            MainWindowResponsiveToolbarPolicy.Evaluate(e.NewSize.Width));

    private void ApplyResponsiveToolbarVisibility(ResponsiveToolbarVisibility visibility)
    {
        // Headless and very early native layout can raise SizeChanged while the
        // generated named-control fields are still being assigned.
        if (toolbarClocks is null ||
            toolbarAlertToneShortcuts is null ||
            toolbarTonesLauncher is null ||
            toolbarOverflowMenu is null)
        {
            return;
        }

        toolbarClocks.IsVisible = visibility.ShowClocks;
        toolbarAlertToneShortcuts.IsVisible = visibility.ShowAlertToneShortcuts;
        toolbarTonesLauncher.IsVisible = visibility.ShowTonesLauncher;
        toolbarOverflowMenu.IsVisible = visibility.ShowOverflow;
    }

    private async void HandleResponsiveAlertToneClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: BuiltInAlertToneViewModel tone })
            await viewModel.SendBuiltInAlertToneAsync(tone).ConfigureAwait(true);
    }
}
