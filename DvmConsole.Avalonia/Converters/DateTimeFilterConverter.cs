// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DvmConsole.Avalonia.Converters
{
    /// <summary>
    /// Bridges Avalonia DatePicker's DateTimeOffset? value to the viewer VM's
    /// WPF-compatible DateTime? local-date filter contract.
    /// </summary>
    public sealed class DateTimeFilterConverter : IValueConverter
    {
        public object? Convert(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture)
        {
            if (value is null)
                return null;
            if (value is DateTime dateTime)
                return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified));
            if (value is DateTimeOffset dateTimeOffset)
                return dateTimeOffset;
            return null;
        }

        public object? ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture)
        {
            if (value is null)
                return null;
            if (value is DateTimeOffset dateTimeOffset)
                return dateTimeOffset.DateTime;
            if (value is DateTime dateTime)
                return dateTime;
            return null;
        }
    }
}
