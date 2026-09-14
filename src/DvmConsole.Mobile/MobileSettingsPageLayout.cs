// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DvmConsole.Mobile;

internal static class MobileSettingsPageLayout
{
    public static Grid Create(Control heading, Control body)
    {
        var page = new Grid
        {
            RowDefinitions = new("Auto,*"),
            RowSpacing = 12,
            Margin = new Thickness(16),
            MaxWidth = 720
        };
        page.Children.Add(heading);
        var scroll = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        page.Children.Add(scroll);
        return page;
    }

    public static Grid Heading(string title, Button back)
    {
        // Keep navigation in its own column so longer titles cannot move it to another row.
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, MinHeight = 56 };
        back.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(back);
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        }.WithScaledFontSize(20);
        Grid.SetColumn(label, 1);
        heading.Children.Add(label);
        return heading;
    }
}
