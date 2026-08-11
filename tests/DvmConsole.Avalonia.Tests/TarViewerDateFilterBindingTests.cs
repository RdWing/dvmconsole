// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Globalization;
using System.IO;
using DvmConsole.Avalonia.Converters;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class TarViewerDateFilterBindingTests
    {
        [Fact]
        public void XamlUsesExplicitDateTimeFilterConverterForBothDatePickers()
        {
            string xaml = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "Views",
                "TarViewerWindow.axaml"));

            Assert.Equal(2, Count(xaml, "Converter={StaticResource DateTimeFilterConverter}"));
            Assert.Contains("DateTimeFilterConverter", xaml);
        }

        [Fact]
        public void ConverterRoundTripsDateTimeAndDateTimeOffsetWithoutChangingTheCalendarDate()
        {
            var converter = new DateTimeFilterConverter();
            DateTime source = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Unspecified);

            object? selectedDate = converter.Convert(source, typeof(DateTimeOffset?), null, CultureInfo.InvariantCulture);
            var offset = Assert.IsType<DateTimeOffset>(selectedDate);
            Assert.Equal(source.Date, offset.Date);

            object? filterDate = converter.ConvertBack(offset, typeof(DateTime?), null, CultureInfo.InvariantCulture);
            Assert.Equal(source.Date, Assert.IsType<DateTime>(filterDate).Date);
            Assert.Null(converter.Convert(null, typeof(DateTimeOffset?), null, CultureInfo.InvariantCulture));
            Assert.Null(converter.ConvertBack(null, typeof(DateTime?), null, CultureInfo.InvariantCulture));
        }

        private static int Count(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
