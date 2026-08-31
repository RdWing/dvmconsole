using Avalonia;
using Avalonia.Controls;

namespace DvmConsole.Desktop;

// Preserves the first visible item while rows are inserted above it. Windows
// supply only their item controls and model projection; capture/restore math
// and lifecycle state remain centralized here.
internal sealed class ScrollViewportAnchor<T> where T : class
{
    private readonly Func<ScrollViewer?> getScrollViewer;
    private readonly Func<IEnumerable<Control>> getItemControls;
    private readonly Func<Control, T?> getItem;
    private T? pendingAnchor;
    private double pendingAnchorY;
    private double pendingExtentHeight;
    private bool restoring;

    public ScrollViewportAnchor(
        Func<ScrollViewer?> getScrollViewer,
        Func<IEnumerable<Control>> getItemControls,
        Func<Control, T?> getItem)
    {
        this.getScrollViewer = getScrollViewer ?? throw new ArgumentNullException(nameof(getScrollViewer));
        this.getItemControls = getItemControls ?? throw new ArgumentNullException(nameof(getItemControls));
        this.getItem = getItem ?? throw new ArgumentNullException(nameof(getItem));
    }

    public void Capture()
    {
        if (pendingAnchor is not null || restoring)
            return;

        ScrollViewer? scrollViewer = getScrollViewer();
        if (scrollViewer is null || scrollViewer.Offset.Y <= 0.5)
            return;

        var visibleItem = getItemControls()
            .Select(control => new
            {
                Control = control,
                Item = getItem(control),
                Position = control.TranslatePoint(default, scrollViewer)
            })
            .Where(candidate =>
                candidate.Item is not null &&
                candidate.Position is Point position &&
                position.Y + candidate.Control.Bounds.Height > 0 &&
                position.Y < scrollViewer.Viewport.Height)
            .OrderBy(candidate => candidate.Position!.Value.Y)
            .FirstOrDefault();

        if (visibleItem?.Item is null || visibleItem.Position is not Point anchorPosition)
            return;

        pendingAnchor = visibleItem.Item;
        pendingAnchorY = anchorPosition.Y;
        pendingExtentHeight = scrollViewer.Extent.Height;
    }

    public void Restore()
    {
        if (pendingAnchor is not T anchor)
            return;

        ScrollViewer? scrollViewer = getScrollViewer();
        if (scrollViewer is null)
            return;

        Control? anchorControl = getItemControls()
            .FirstOrDefault(control => ReferenceEquals(getItem(control), anchor));
        Point? anchorPosition = anchorControl?.TranslatePoint(default, scrollViewer);
        double anchorDelta = anchorPosition is Point position
            ? position.Y - pendingAnchorY
            : 0;
        double extentDelta = scrollViewer.Extent.Height - pendingExtentHeight;
        double? resolvedDelta = ScrollViewportAnchorMath.ResolveLayoutDelta(
            anchorPosition is not null,
            anchorDelta,
            extentDelta);
        if (resolvedDelta is not double itemDelta)
        {
            // ItemsControl layout can notify before its containing ScrollViewer
            // incorporates a newly inserted top row. Keep the anchor until a
            // later layout pass exposes either the row or extent movement.
            return;
        }

        pendingAnchor = null;
        double desiredOffset = ScrollViewportAnchorMath.CalculateOffset(
            scrollViewer.Offset.Y,
            itemDelta,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height);
        restoring = true;
        try
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, desiredOffset);
        }
        finally
        {
            restoring = false;
        }
    }

    public void Reset()
    {
        pendingAnchor = null;
        restoring = false;
    }
}

internal static class ScrollViewportAnchorMath
{
    public static double? ResolveLayoutDelta(
        bool anchorWasLocated,
        double anchorDelta,
        double extentDelta)
    {
        if (anchorWasLocated && Math.Abs(anchorDelta) > 0.25)
            return anchorDelta;
        if (Math.Abs(extentDelta) > 0.25)
            return extentDelta;
        return null;
    }

    public static double CalculateOffset(
        double currentOffset,
        double itemDelta,
        double extentHeight,
        double viewportHeight)
    {
        double maximumOffset = Math.Max(0, extentHeight - viewportHeight);
        return Math.Clamp(currentOffset + itemDelta, 0, maximumOffset);
    }
}
