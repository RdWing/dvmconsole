// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;

namespace DvmConsole.Desktop;

internal static class ResponsiveStatusPolicy
{
    internal const double CompactStatusMaximumWidth = 1_000;

    public static bool IsCompact(double availableWidth, double uiScale)
    {
        double width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        double scale = double.IsFinite(uiScale) && uiScale > 0 ? uiScale : 1;
        return width / scale < CompactStatusMaximumWidth;
    }
}

/// <summary>
/// Owns the shell status presentation breakpoint. The bound status content
/// remains in the view model; this controller only selects its wide or compact
/// presentation.
/// </summary>
internal sealed class ResponsiveStatusController
{
    private readonly Control fullStatus;
    private readonly Control compactStatus;

    public ResponsiveStatusController(Control fullStatus, Control compactStatus)
    {
        this.fullStatus = fullStatus ?? throw new ArgumentNullException(nameof(fullStatus));
        this.compactStatus = compactStatus ?? throw new ArgumentNullException(nameof(compactStatus));
    }

    public bool IsCompact { get; private set; }

    public void Apply(double availableWidth, double uiScale)
    {
        IsCompact = ResponsiveStatusPolicy.IsCompact(availableWidth, uiScale);
        fullStatus.IsVisible = !IsCompact;
        compactStatus.IsVisible = IsCompact;
    }
}
