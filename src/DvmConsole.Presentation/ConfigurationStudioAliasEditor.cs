// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal static class ConfigurationStudioAliasEditor
{
    public static IReadOnlyList<ConfigurationAliasRow> ProjectRows(
        IReadOnlyDictionary<string, List<RadioAlias>> aliasTables)
        => aliasTables
            .SelectMany(table => table.Value.Select(alias => new ConfigurationAliasRow(table.Key, alias)))
            .ToArray();

    public static string CreateManagedCompanionReference(
        string suggestedName,
        IEnumerable<string?> reservedReferences)
    {
        string fileName = GetPortableFileName(suggestedName);
        if (fileName.Length == 0 || fileName is "." or "..")
            throw new InvalidDataException("The selected document does not have a usable file name.");

        var reserved = reservedReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => GetPortableFileName(reference!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!reserved.Contains(fileName))
            return fileName;

        int extensionIndex = fileName.LastIndexOf('.');
        string extension = extensionIndex > 0 ? fileName[extensionIndex..] : string.Empty;
        string stem = extensionIndex > 0 ? fileName[..extensionIndex] : fileName;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{stem}-{suffix}{extension}";
            if (!reserved.Contains(candidate))
                return candidate;
        }
    }

    public static string? FindTableIdentifier(
        string reference,
        IReadOnlyDictionary<string, List<RadioAlias>> aliasTables,
        IReadOnlyDictionary<string, string> referenceIdentifiers)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        if (aliasTables.ContainsKey(reference))
            return reference;
        if (referenceIdentifiers.TryGetValue(reference, out string? identifier) &&
            aliasTables.ContainsKey(identifier))
        {
            return identifier;
        }

        string fileName = GetPortableFileName(reference);
        string[] matches = aliasTables.Keys
            .Where(identifier => string.Equals(
                GetPortableFileName(identifier),
                fileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static uint NextAvailableRid(IReadOnlyCollection<RadioAlias> aliases)
    {
        var used = aliases.Select(alias => alias.Rid).ToHashSet();
        for (uint candidate = 1; candidate < uint.MaxValue; candidate++)
        {
            if (!used.Contains(candidate))
                return candidate;
        }
        if (!used.Contains(uint.MaxValue))
            return uint.MaxValue;
        throw new InvalidOperationException("The selected alias file has no available RID values.");
    }

    private static string GetPortableFileName(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }
}
