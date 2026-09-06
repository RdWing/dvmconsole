// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using DvmConsole.Core.IO;

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
        directory = Path.GetFullPath(directory);
        if (!FileSystemPathIdentity.IsUnderRoot(descriptor.RootPath, directory))
            throw new InvalidDataException("Recording directory must remain inside its root.");
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

    private static string SanitizeSegment(string value) => PortableFileName.Segment(value);
}
