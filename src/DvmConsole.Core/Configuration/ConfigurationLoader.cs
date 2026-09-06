// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Configuration;

public static class ConfigurationLoader
{
    public static ConsoleConfiguration Load(string path)
    {
        ConsoleConfiguration configuration = YamlConfigurationReader.Read(path);
        ConfigurationNormalizer.Normalize(configuration);
        ConfigurationPathHydrator.LoadOptionalAliases(configuration);
        return configuration;
    }

    public static IReadOnlyList<string> Validate(ConsoleConfiguration configuration)
        => ConfigurationValidator.Validate(configuration);

    public static IReadOnlyList<ConfigurationValidationIssue> ValidateDetailed(ConsoleConfiguration configuration)
        => ConfigurationValidator.ValidateDetailed(configuration);

    public static string ResolvePath(ConsoleConfiguration configuration, string? configuredPath)
        => ConfigurationPathHydrator.ResolvePath(configuration, configuredPath);
}
