// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DvmConsole.Mobile;

/// <summary>Shared mobile grouping and spacing; controls retain their existing owners and handlers.</summary>
internal static class MobileSettingsSurface
{
    public static Border Group(IEnumerable<Control> controls, bool paddedRows = false)
    {
        var rows = controls.ToArray();
        var body = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var divider = new Border { Height = 1, Margin = new Thickness(14, 0) };
                divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("ControlBorderBrush"));
                body.Children.Add(divider);
            }
            if (rows[i] is Button button)
            {
                button.Classes.Add("grouped-navigation");
                button.CornerRadius = new CornerRadius(i == 0 ? 12 : 0, i == 0 ? 12 : 0,
                    i == rows.Length - 1 ? 12 : 0, i == rows.Length - 1 ? 12 : 0);
            }
            body.Children.Add(paddedRows
                ? new Border { Padding = new Thickness(14, 8), Child = rows[i] }
                : rows[i]);
        }
        var group = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = body
        };
        group.Bind(Border.BackgroundProperty, group.GetResourceObservable("CardBackgroundBrush"));
        group.Bind(Border.BorderBrushProperty, group.GetResourceObservable("ControlBorderBrush"));
        return group;
    }

    public static void GroupSection(StackPanel section)
    {
        var rows = section.Children.Skip(1).ToArray();
        foreach (var row in rows) section.Children.Remove(row);
        if (rows.Length != 0) section.Children.Add(Group(rows));
    }

    public static TextBlock Caption(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 12, 8, 6),
        Opacity = 0.75
    }.WithScaledFontSize(12);
}
