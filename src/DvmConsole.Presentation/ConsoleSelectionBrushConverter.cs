// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace DvmConsole.Presentation;

/// <summary>Uses the desktop card palette for snapshot selections in other layouts.</summary>
public sealed class ConsoleSelectionBrushConverter : IMultiValueConverter
{
    public ConsoleCardSelection Kind { get; set; }
    public bool Border { get; set; }

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => ConsoleCardPalette.Selection(values.Count > 1 && values[1] is ThemeVariant theme && theme == ThemeVariant.Dark,
            Kind, values.Count > 0 && values[0] is true, Border, values.Count > 2 && values[2] is true);
}
