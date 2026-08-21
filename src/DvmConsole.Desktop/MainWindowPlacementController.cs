using Avalonia;
using Avalonia.Controls;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

// Keeps platform window mechanics out of settings persistence and the main
// window's operator-workflow code.
internal sealed class MainWindowPlacementController : IDisposable
{
    private const int MinimumVisibleTitleWidth = 64;
    private const int MinimumVisibleTitleHeight = 24;
    private const int TitleBarHeight = 48;

    private readonly Window window;
    private readonly WindowPlacementSetting initialPlacement;
    private WindowPlacementSetting? lastNormalPlacement;
    private bool tracking;

    public MainWindowPlacementController(Window window, WindowPlacementSetting initialPlacement)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.initialPlacement = Copy(initialPlacement ?? throw new ArgumentNullException(nameof(initialPlacement)));
    }

    public void PrepareSize()
    {
        window.Width = initialPlacement.Width;
        window.Height = initialPlacement.Height;
    }

    public void RestorePosition()
    {
        if (IsVisibleOnCurrentDisplays(initialPlacement))
        {
            window.Position = new PixelPoint(
                (int)Math.Round(initialPlacement.Left!.Value),
                (int)Math.Round(initialPlacement.Top!.Value));
        }

        CaptureNormalPlacement();
    }

    public void StartTracking()
    {
        if (tracking)
            return;

        tracking = true;
        window.PositionChanged += HandleWindowBoundsChanged;
        window.Resized += HandleWindowBoundsChanged;
        CaptureNormalPlacement();
    }

    public WindowPlacementSetting GetPlacementForPersistence()
    {
        CaptureNormalPlacement();
        return Copy(lastNormalPlacement ?? initialPlacement);
    }

    public void Dispose()
    {
        if (!tracking)
            return;

        tracking = false;
        window.PositionChanged -= HandleWindowBoundsChanged;
        window.Resized -= HandleWindowBoundsChanged;
    }

    internal static bool HasUsableTitleBarIntersection(
        WindowPlacementSetting placement,
        PixelRect workingArea,
        double displayScaling)
    {
        if (placement.Left is not double left || placement.Top is not double top ||
            !double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(displayScaling) || displayScaling <= 0)
        {
            return false;
        }

        int titleLeft = (int)Math.Round(left);
        int titleTop = (int)Math.Round(top);
        int titleWidth = Math.Max(1, (int)Math.Ceiling(placement.Width * displayScaling));
        int titleHeight = Math.Max(1, (int)Math.Ceiling(TitleBarHeight * displayScaling));
        int intersectionWidth = Math.Max(
            0,
            Math.Min(titleLeft + titleWidth, workingArea.Right) - Math.Max(titleLeft, workingArea.X));
        int intersectionHeight = Math.Max(
            0,
            Math.Min(titleTop + titleHeight, workingArea.Bottom) - Math.Max(titleTop, workingArea.Y));

        return intersectionWidth >= MinimumVisibleTitleWidth &&
            intersectionHeight >= MinimumVisibleTitleHeight;
    }

    private bool IsVisibleOnCurrentDisplays(WindowPlacementSetting placement)
        => window.Screens.All.Any(screen =>
            HasUsableTitleBarIntersection(placement, screen.WorkingArea, screen.Scaling));

    private void HandleWindowBoundsChanged(object? sender, EventArgs e)
        => CaptureNormalPlacement();

    private void CaptureNormalPlacement()
    {
        if (window.WindowState != WindowState.Normal ||
            !double.IsFinite(window.ClientSize.Width) || window.ClientSize.Width <= 0 ||
            !double.IsFinite(window.ClientSize.Height) || window.ClientSize.Height <= 0)
        {
            return;
        }

        lastNormalPlacement = new WindowPlacementSetting
        {
            Left = window.Position.X,
            Top = window.Position.Y,
            Width = window.ClientSize.Width,
            Height = window.ClientSize.Height
        };
    }

    private static WindowPlacementSetting Copy(WindowPlacementSetting placement)
        => new()
        {
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height
        };
}
