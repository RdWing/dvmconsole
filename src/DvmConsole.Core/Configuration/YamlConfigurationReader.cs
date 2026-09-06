// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Configuration;

using DvmConsole.Core.IO;

internal static class YamlConfigurationReader
{
    public static ConsoleConfiguration Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The codeplug file was not found.", fullPath);

        ConsoleConfiguration configuration = DvmYamlCodec.ParseConfiguration(
            BoundedResourceReader.ReadUtf8File(
                fullPath,
                ManagedResourceLimits.ConfigurationYamlBytes,
                "Configuration YAML"));
        configuration.SourcePath = fullPath;
        return configuration;
    }
}
