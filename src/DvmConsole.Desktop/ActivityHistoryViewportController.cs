// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns Activity history viewport anchoring and its temporary layout hook.
/// Session replacement and shutdown can reset the complete behavior through a
/// single idempotent boundary.
/// </summary>
internal sealed class ActivityHistoryViewportController : IDisposable
{
    private readonly Control layoutSurface;
    private readonly IScrollViewportAnchor anchor;
    private bool layoutHookAttached;

    public ActivityHistoryViewportController(
        Control layoutSurface,
        IScrollViewportAnchor anchor)
    {
        this.layoutSurface = layoutSurface ?? throw new ArgumentNullException(nameof(layoutSurface));
        this.anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
    }

    public void HandleCollectionChanging(
        object? sender,
        NotifyCollectionChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Action == NotifyCollectionChangedAction.Reset)
        {
            Reset();
            return;
        }

        if (change.Action != NotifyCollectionChangedAction.Add || change.NewStartingIndex != 0)
            return;

        anchor.Capture();
        if (anchor.HasPendingRestore)
            AttachLayoutHook();
    }

    internal void RestoreAfterLayout()
    {
        anchor.Restore();
        if (!anchor.HasPendingRestore)
            DetachLayoutHook();
    }

    public void Reset()
    {
        anchor.Reset();
        DetachLayoutHook();
    }

    public void Dispose() => Reset();

    private void AttachLayoutHook()
    {
        if (layoutHookAttached)
            return;
        layoutSurface.LayoutUpdated += HandleLayoutUpdated;
        layoutHookAttached = true;
    }

    private void DetachLayoutHook()
    {
        if (!layoutHookAttached)
            return;
        layoutSurface.LayoutUpdated -= HandleLayoutUpdated;
        layoutHookAttached = false;
    }

    private void HandleLayoutUpdated(object? sender, EventArgs e)
        => RestoreAfterLayout();
}
