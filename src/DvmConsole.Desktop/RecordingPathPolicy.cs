using System.Globalization;

namespace DvmConsole.Desktop;

internal sealed class RecordingPathPolicy
{
    public string CreatePath(RecordingFinalizationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        DateTimeOffset localStart = descriptor.UtcStartTime.ToLocalTime();
        string dateFolder = localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string security = SecuritySegment(descriptor);
        string filename = string.Join(
            "_",
            localStart.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            SanitizeSegment(descriptor.SystemName),
            descriptor.TalkgroupId.ToString(CultureInfo.InvariantCulture),
            (descriptor.SourceId ?? 0).ToString(CultureInfo.InvariantCulture),
            security,
            descriptor.StreamId.ToString(CultureInfo.InvariantCulture));
        string directory = Path.Combine(
            descriptor.RootPath,
            dateFolder,
            SanitizeSegment(descriptor.SystemName));
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"{filename}.opus");
        for (int suffix = 1; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{filename}-{suffix}.opus");
        return path;
    }

    private static string SecuritySegment(RecordingFinalizationDescriptor descriptor)
    {
        EncryptionSnapshot encryption = descriptor.Encryption;
        if (!encryption.IsKnown)
            return "UNKNOWN";
        if (!encryption.IsSecure)
            return "CLEAR";

        string algorithm = EncryptionPresentation.AlgorithmAbbreviation(
            descriptor.Protocol,
            encryption.AlgorithmId);
        return string.IsNullOrEmpty(algorithm) ? "SECURE" : $"SECURE_{algorithm}";
    }

    private static string SanitizeSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        sanitized = string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim();
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }
}
