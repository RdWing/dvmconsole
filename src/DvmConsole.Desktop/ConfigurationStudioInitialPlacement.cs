using Avalonia;

namespace DvmConsole.Desktop;

internal readonly record struct ConfigurationStudioInitialPlacement(
    Size Size,
    PixelPoint Position)
{
    private const double WorkingAreaMarginDip = 48;

    public static ConfigurationStudioInitialPlacement FitToWorkingArea(
        Size requestedSize,
        PixelRect workingArea,
        double displayScaling)
    {
        if (!double.IsFinite(displayScaling) || displayScaling <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayScaling));
        if (!double.IsFinite(requestedSize.Width) || requestedSize.Width <= 0 ||
            !double.IsFinite(requestedSize.Height) || requestedSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedSize));
        }

        double availableWidth = Math.Max(1, workingArea.Width / displayScaling - WorkingAreaMarginDip);
        double availableHeight = Math.Max(1, workingArea.Height / displayScaling - WorkingAreaMarginDip);
        var size = new Size(
            Math.Min(requestedSize.Width, availableWidth),
            Math.Min(requestedSize.Height, availableHeight));
        int widthPixels = (int)Math.Ceiling(size.Width * displayScaling);
        int heightPixels = (int)Math.Ceiling(size.Height * displayScaling);
        var position = new PixelPoint(
            workingArea.X + Math.Max(0, (workingArea.Width - widthPixels) / 2),
            workingArea.Y + Math.Max(0, (workingArea.Height - heightPixels) / 2));

        return new ConfigurationStudioInitialPlacement(size, position);
    }
}
