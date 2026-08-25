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
    // These thresholds begin at the compact header's natural width. Alert
    // shortcuts shed first, followed by the tones launcher and then clocks.
    // This keeps the right edge within the window while retaining clocks beside
    // the operational controls for as long as possible.
    internal const double ToolbarClocksMinimumWidth = 920;
    internal const double TonesLauncherMinimumWidth = 1_000;
    internal const double AlertToneShortcutsMinimumWidth = 1_120;
    internal const double AdditionalClockWidth = 80;

    public static ResponsiveToolbarVisibility Evaluate(
        double availableWidth,
        double uiScale = 1,
        int enabledClockCount = 1)
    {
        double width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        double scale = double.IsFinite(uiScale) && uiScale > 0 ? uiScale : 1;
        double logicalWidth = width / scale;
        double additionalClockWidth = Math.Max(0, enabledClockCount - 1) * AdditionalClockWidth;
        bool showClocks = logicalWidth >= ToolbarClocksMinimumWidth + additionalClockWidth;
        bool showAlertToneShortcuts = logicalWidth >= AlertToneShortcutsMinimumWidth + additionalClockWidth;
        bool showTonesLauncher = logicalWidth >= TonesLauncherMinimumWidth + additionalClockWidth;

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
        => RefreshResponsiveToolbarVisibility(e.NewSize.Width);

    private void RefreshResponsiveToolbarVisibility(double availableWidth)
    {
        MainWindowViewModel? currentViewModel = DataContext as MainWindowViewModel;
        double uiScale = currentViewModel?.UiScale ?? 1;
        int enabledClockCount = currentViewModel?.ToolbarClocks.Count(clock => clock.Enabled) ?? 1;
        ApplyResponsiveToolbarVisibility(MainWindowResponsiveToolbarPolicy.Evaluate(
            availableWidth,
            uiScale,
            enabledClockCount));
    }

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
