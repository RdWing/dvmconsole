// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal static class ConfigurationStudioValidationCollaborator
{
    public static IReadOnlyList<ConfigurationValidationIssue> BuildIssues(
        IEnumerable<ConfigurationValidationIssue> documentIssues,
        KeyContainer keys,
        IReadOnlyList<SystemConfiguration> systems,
        bool selectedKeyIdInputInvalid,
        KeyEntry? selectedKey,
        string? keyFileLoadError,
        bool keyFileLoadIsWarning,
        IEnumerable<string> aliasLoadErrors,
        IEnumerable<string> aliasLoadWarnings,
        IReadOnlyDictionary<string, List<RadioAlias>> aliasTables)
    {
        var issues = new List<ConfigurationValidationIssue>();
        issues.AddRange(documentIssues);
        issues.AddRange(ValidateKeys(
            keys,
            systems,
            selectedKeyIdInputInvalid,
            selectedKey));

        if (keyFileLoadError is not null)
        {
            issues.Add(new ConfigurationValidationIssue(
                keyFileLoadIsWarning
                    ? ConfigurationValidationSeverity.Warning
                    : ConfigurationValidationSeverity.Error,
                "Encryption Keys",
                "keyFile",
                keyFileLoadError));
        }
        issues.AddRange(aliasLoadErrors.Select(error => new ConfigurationValidationIssue(
            ConfigurationValidationSeverity.Error,
            "Files & Interoperability",
            "aliasPath",
            error)));
        issues.AddRange(aliasLoadWarnings.Select(warning => new ConfigurationValidationIssue(
            ConfigurationValidationSeverity.Warning,
            "Files & Interoperability",
            "aliasPath",
            warning)));
        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
        {
            foreach (IGrouping<uint, RadioAlias> duplicate in table.Value
                         .GroupBy(alias => alias.Rid)
                         .Where(group => group.Count() > 1))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationSeverity.Error,
                    "Files & Interoperability",
                    table.Key,
                    $"RID {duplicate.Key} is duplicated in '{table.Key}'."));
            }
        }
        return issues;
    }

    private static IEnumerable<ConfigurationValidationIssue> ValidateKeys(
        KeyContainer keys,
        IReadOnlyList<SystemConfiguration> systems,
        bool selectedKeyIdInputInvalid,
        KeyEntry? selectedKey)
    {
        foreach (ConfigurationValidationIssue issue in KeyFileValidator.Validate(keys))
            yield return issue;
        for (int index = 0; index < keys.Keys.Count; index++)
        {
            KeyEntry key = keys.Keys[index];
            if (string.IsNullOrWhiteSpace(key.System))
            {
                yield return new ConfigurationValidationIssue(
                    ConfigurationValidationSeverity.Warning,
                    "Encryption Keys",
                    $"keys[{index}].system",
                    $"Key {index + 1} is a legacy unscoped key available to every FNE system. Assign one FNE system to isolate it.");
            }
            else if (!systems.Any(system =>
                         string.Equals(system.Name, key.System.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                yield return new ConfigurationValidationIssue(
                    ConfigurationValidationSeverity.Error,
                    "Encryption Keys",
                    $"keys[{index}].system",
                    $"Key {index + 1} references FNE system '{key.System}', which is not defined in this codeplug.");
            }
        }

        if (selectedKeyIdInputInvalid && selectedKey is not null)
        {
            int index = keys.Keys.IndexOf(selectedKey);
            if (index >= 0)
            {
                yield return new ConfigurationValidationIssue(
                    ConfigurationValidationSeverity.Error,
                    "Encryption Keys",
                    $"keys[{index}].keyId",
                    $"Key {index + 1} must use a hexadecimal key ID.");
            }
        }
    }
}
