// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Configuration;

using DvmConsole.Core.IO;

public static class AliasFileLoader
{
    public static List<RadioAlias> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Alias file not found.", fullPath);

        return Parse(BoundedResourceReader.ReadUtf8File(
            fullPath,
            ManagedResourceLimits.ConfigurationCompanionBytes,
            "Alias file"));
    }

    public static List<RadioAlias> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return DvmYamlCodec.ParseAliases(yaml);
    }

    public static string FindAlias(IEnumerable<RadioAlias>? aliases, uint rid)
    {
        return aliases is RadioAliasIndex index
            ? index.Find(rid)
            : aliases?.FirstOrDefault(alias => alias.Rid == rid)?.Alias ?? string.Empty;
    }

    public static string Serialize(IEnumerable<RadioAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        return DvmYamlCodec.SerializeAliases(aliases);
    }
}
