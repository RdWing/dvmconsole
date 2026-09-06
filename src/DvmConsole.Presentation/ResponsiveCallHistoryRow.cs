// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Controls.Metadata;

namespace DvmConsole.Presentation;

// Owns the history row's one responsive width state. Its descendants react
// through XAML styles, so the containing view never scans realized rows.
[PseudoClasses(":narrow")]
public sealed class ResponsiveCallHistoryRow : Grid
{
    private const double NarrowWidth = 720;
    private bool? isNarrow;

    public ResponsiveCallHistoryRow()
    {
        ColumnSpacing = 7;
        RowSpacing = 3;
        SizeChanged += (_, _) => UpdateWidthState();
    }

    private void UpdateWidthState()
    {
        bool narrow = Bounds.Width is > 0 and < NarrowWidth;
        if (isNarrow == narrow)
            return;

        isNarrow = narrow;
        ColumnDefinitions = new ColumnDefinitions(narrow ? "80,*" : "80,*,Auto");
        RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto");
        PseudoClasses.Set(":narrow", narrow);
    }
}
