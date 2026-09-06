// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;

namespace DvmConsole.Presentation;

internal static class ResponsiveSettingsDensity
{
    private const double NarrowWidthThreshold = 400;
    private const string NarrowClass = "narrow-settings";

    public static void Attach(UserControl view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.SizeChanged += HandleSizeChanged;
        Update(view);
    }

    private static void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is UserControl view)
            Update(view);
    }

    private static void Update(UserControl view)
    {
        bool isNarrow = view.Bounds.Width > 0 && view.Bounds.Width < NarrowWidthThreshold;
        bool hasClass = view.Classes.Contains(NarrowClass);
        if (isNarrow == hasClass)
            return;

        if (isNarrow)
            view.Classes.Add(NarrowClass);
        else
            view.Classes.Remove(NarrowClass);
    }
}
