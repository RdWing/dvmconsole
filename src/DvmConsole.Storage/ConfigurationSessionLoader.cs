// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Storage;

public sealed record ConsoleTopology(
    ConsoleConfiguration Configuration,
    string CodeplugPath,
    IReadOnlyList<string> ValidationErrors,
    ConfigurationReference? ConfigurationReference = null,
    bool MigrateLegacyConfigurationOperatorState = false)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

public sealed record ConsoleSessionLoadResult(string StatusText, ConsoleTopology? Topology);

/// <summary>Loads and validates an app-owned materialized configuration for any host.</summary>
public static class ConfigurationSessionLoader
{
    public static ConsoleSessionLoadResult Load(string? configurationPath,
        ConfigurationReference? configurationReference = null,
        bool migrateLegacyConfigurationOperatorState = false)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new ConsoleSessionLoadResult(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                null);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). Connections are idle until Connect is pressed."
                : $"Configuration has {errors.Count} validation error(s):\n• {string.Join("\n• ", errors)}";
            string loadedCodeplugPath = configuration.SourcePath ?? Path.GetFullPath(configurationPath);
            return new ConsoleSessionLoadResult(
                status,
                new ConsoleTopology(
                    configuration,
                    loadedCodeplugPath,
                    errors.ToArray(),
                    configurationReference,
                    migrateLegacyConfigurationOperatorState));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            return new ConsoleSessionLoadResult($"Unable to load codeplug: {exception.Message}", null);
        }
    }
}
