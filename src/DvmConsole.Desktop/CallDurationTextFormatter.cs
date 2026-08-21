using System.Globalization;

namespace DvmConsole.Desktop;

internal static class CallDurationTextFormatter
{
    public static string Format(TimeSpan duration)
    {
        long totalTenths = Math.Max(
            0,
            (long)Math.Round(duration.TotalSeconds * 10, MidpointRounding.AwayFromZero));
        long hours = totalTenths / 36_000;
        long minutes = totalTenths % 36_000 / 600;
        long seconds = totalTenths % 600 / 10;
        long tenths = totalTenths % 10;

        if (hours >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{hours}h {minutes:00}m {seconds:00}.{tenths}s");
        }

        if (minutes >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{minutes}m {seconds:00}.{tenths}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds}.{tenths}s");
    }
}
