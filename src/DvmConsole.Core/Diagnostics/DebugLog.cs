// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Diagnostics;

using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;

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

public static partial class DebugLogRedactor
{
    public static string Redact(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Contains("Network Sent", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Network Received", StringComparison.OrdinalIgnoreCase))
            return "[network payload redacted]";

        if (message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("preshared", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("key material", StringComparison.OrdinalIgnoreCase))
        {
            return "[sensitive diagnostic message redacted]";
        }

        return message;
    }

    public static string RedactForExport(string message)
    {
        string redacted = Redact(message);
        redacted = SystemPrefixRegex().Replace(redacted, "(system)");
        redacted = UriRegex().Replace(redacted, "[endpoint]");
        redacted = PathRegex().Replace(redacted, "[path]");
        redacted = Ipv6EndpointRegex().Replace(redacted, match =>
        {
            string address = match.Groups["address"].Value;
            int scope = address.IndexOf('%');
            if (scope >= 0)
                address = address[..scope];
            return IPAddress.TryParse(address, out IPAddress? parsed) &&
                parsed.AddressFamily == AddressFamily.InterNetworkV6 ? "[endpoint]" : match.Value;
        });
        redacted = Ipv4EndpointRegex().Replace(redacted, "[endpoint]");
        redacted = HostnameEndpointRegex().Replace(redacted, "[endpoint]");
        return IdentifierRegex().Replace(redacted, "$1 [redacted]");
    }

    [GeneratedRegex(@"^\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex SystemPrefixRegex();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"[""'](?:[a-z]:[\\/]|/|\\\\)[^""'\r\n]*[""']|(?<![\w])(?:[a-z]:[\\/]|/|\\\\)[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"(?<![\w:])(?:\[(?<address>[0-9a-f:.]+(?:%[\w.-]+)?)\](?::\d{1,5})?|(?<address>[0-9a-f]*:[0-9a-f:.]+(?:%[\w.-]+)?))(?![\w:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6EndpointRegex();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4EndpointRegex();

    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d{1,5})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostnameEndpointRegex();

    [GeneratedRegex(@"\b(peer(?:\s+id)?|rid|tg(?:id)?|stream(?:\s+id)?)\s*[:#=]?\s*(?:0x[0-9a-f]+|\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
