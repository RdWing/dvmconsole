namespace DvmConsole.Core.Diagnostics;

public enum DebugLogSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public sealed record DebugLogEntry(
    DateTimeOffset Timestamp,
    string Source,
    DebugLogSeverity Severity,
    string Message)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string SeverityText => Severity.ToString().ToUpperInvariant();
    public string Summary => $"{TimestampText} [{SeverityText}] {Source}: {Message}";
}

public static class DebugLogRedactor
{
    public static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Contains("Network Sent", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Network Received", StringComparison.OrdinalIgnoreCase))
        {
            int separator = message.IndexOf(" -- ", StringComparison.Ordinal);
            if (separator >= 0)
                return $"{message[..separator]} -- [payload redacted]";

            return "[network payload redacted]";
        }

        if (message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("preshared", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("key material", StringComparison.OrdinalIgnoreCase))
        {
            return "[sensitive diagnostic message redacted]";
        }

        return message;
    }
}
