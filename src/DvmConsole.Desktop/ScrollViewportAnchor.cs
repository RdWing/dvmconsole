// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;

namespace DvmConsole.Desktop;

internal interface IScrollViewportAnchor
{
    bool HasPendingRestore { get; }
    void Capture();
    void Restore();
    void Reset();
}

// Preserves the first visible item while rows are inserted above it. Windows
// supply only their item controls and model projection; capture/restore math
// and lifecycle state remain centralized here.
internal sealed class ScrollViewportAnchor<T> : IScrollViewportAnchor where T : class
{
    private readonly Func<ScrollViewer?> getScrollViewer;
    private readonly Func<IEnumerable<Control>> getItemControls;
    private readonly Func<Control, T?> getItem;
    private readonly Func<T, bool>? itemExists;
    private readonly Action<T>? realizeItem;
    private T? pendingAnchor;
    private double pendingAnchorY;
    private double pendingExtentHeight;
    private double pendingOffset;
    private bool restoring;
    private bool offsetRestoreRequested;

    public ScrollViewportAnchor(
        Func<ScrollViewer?> getScrollViewer,
        Func<IEnumerable<Control>> getItemControls,
        Func<Control, T?> getItem,
        Func<T, bool>? itemExists = null,
        Action<T>? realizeItem = null)
    {
        this.getScrollViewer = getScrollViewer ?? throw new ArgumentNullException(nameof(getScrollViewer));
        this.getItemControls = getItemControls ?? throw new ArgumentNullException(nameof(getItemControls));
        this.getItem = getItem ?? throw new ArgumentNullException(nameof(getItem));
        this.itemExists = itemExists;
        this.realizeItem = realizeItem;
    }

    public bool HasPendingRestore => pendingAnchor is not null;

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
        pendingOffset = scrollViewer.Offset.Y;
    }

    public void Restore()
    {
        if (pendingAnchor is not T anchor)
            return;

        ScrollViewer? scrollViewer = getScrollViewer();
        if (scrollViewer is null)
            return;
        if (itemExists?.Invoke(anchor) == false)
        {
            // A retained-history limit can evict the record itself. There is
            // no longer an identity to restore; do not retain a layout hook.
            Reset();
            return;
        }

        Control? anchorControl = getItemControls()
            .FirstOrDefault(control => ReferenceEquals(getItem(control), anchor));
        if (anchorControl is null && realizeItem is not null)
        {
            // A burst can move the record outside the virtualized containers.
            // Realize its identity before measuring, rather than guessing from
            // total extent (which may stay unchanged when old rows are evicted).
            realizeItem(anchor);
            return;
        }
        Point? anchorPosition = anchorControl?.TranslatePoint(default, scrollViewer);
        double anchorDelta = anchorPosition is Point position
            ? position.Y - pendingAnchorY
            : 0;
        double extentDelta = scrollViewer.Extent.Height - pendingExtentHeight;
        if ((offsetRestoreRequested || Math.Abs(scrollViewer.Offset.Y - pendingOffset) > 0.25) &&
            anchorPosition is not null && Math.Abs(anchorDelta) <= 0.25)
        {
            Reset();
            return;
        }
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

        double desiredOffset = ScrollViewportAnchorMath.CalculateOffset(
            scrollViewer.Offset.Y,
            itemDelta,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height);
        if (Math.Abs(desiredOffset - scrollViewer.Offset.Y) <= 0.25)
        {
            Reset();
            return;
        }
        // Offset changes are arranged in a subsequent layout pass. Retain the
        // original item until that pass confirms its pixel position; another
        // prepend in between must not capture the stale rendered viewport.
        offsetRestoreRequested = anchorPosition is not null;
        if (!offsetRestoreRequested) pendingAnchor = null;
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
        offsetRestoreRequested = false;
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
            return anchorWasLocated ? 0 : extentDelta;
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
