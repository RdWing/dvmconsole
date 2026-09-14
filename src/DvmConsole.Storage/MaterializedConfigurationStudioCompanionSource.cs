// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security;
using DvmConsole.Core.Configuration;
using DvmConsole.Application;

namespace DvmConsole.Storage;

/// <summary>
/// Resolves companion content from an app-owned materialized Configuration Studio workspace.
/// Imported external files remain outside the live editing transaction.
/// </summary>
public sealed class MaterializedConfigurationStudioCompanionSource : IConfigurationStudioCompanionSource
{
    public string CreateWebStreamAuthorizationIdentity(
        string hostDocumentIdentity,
        WebStreamConfiguration stream)
        => ConfigurationWebStreamAuthorizationIdentity.Create(hostDocumentIdentity, stream);

    public ConfigurationDocument ParseDraft(string yaml, ConfigurationDocument currentDocument)
    {
        ArgumentNullException.ThrowIfNull(currentDocument);
        return ConfigurationDocument.Parse(yaml, currentDocument.SourcePath);
    }

    public ConfigurationDocument AcceptSaved(
        ConfigurationDocument currentDocument,
        string hostDocumentIdentity,
        string yaml)
    {
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDocumentIdentity);
        currentDocument.AcceptSaved(hostDocumentIdentity, yaml);
        return currentDocument;
    }

    public ConfigurationStudioCompanionSnapshot Load(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ConfigurationStudioKeyCompanion? keyFile = LoadKeyFile(document);
        var aliases = new Dictionary<string, ConfigurationStudioAliasCompanion>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (document.SourcePath is not null)
        {
            foreach (SystemConfiguration system in document.Configuration.Systems)
            {
                if (string.IsNullOrWhiteSpace(system.AliasPath))
                    continue;
                try
                {
                    string identifier = ConfigurationLoader.ResolvePath(
                        document.Configuration,
                        system.AliasPath);
                    if (aliases.TryGetValue(identifier, out ConfigurationStudioAliasCompanion? existing))
                    {
                        aliases[identifier] = existing with
                        {
                            References = existing.References
                                .Append(system.AliasPath)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                        };
                        continue;
                    }
                    if (!File.Exists(identifier))
                    {
                        aliases[identifier] = new ConfigurationStudioAliasCompanion(
                            identifier,
                            [system.AliasPath],
                            string.Empty,
                            null);
                        warnings.Add($"Alias file for system '{system.Name}' does not exist yet: {Path.GetFileName(identifier)}");
                        continue;
                    }
                    List<RadioAlias> loaded = AliasFileLoader.Load(identifier);
                    aliases[identifier] = new ConfigurationStudioAliasCompanion(
                        identifier,
                        [system.AliasPath],
                        AliasFileLoader.Serialize(loaded),
                        ConfigurationDocument.ComputeFileHash(identifier));
                }
                catch (Exception exception) when (IsCompanionLoadFailure(exception))
                {
                    errors.Add($"Alias file '{Path.GetFileName(system.AliasPath)}' for system '{system.Name}' could not be opened: {DescribeFailure(exception)}");
                }
            }
        }

        return new ConfigurationStudioCompanionSnapshot(
            keyFile,
            aliases.Values.ToArray(),
            errors,
            warnings);
    }

    private static ConfigurationStudioKeyCompanion? LoadKeyFile(ConfigurationDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Configuration.KeyFile) || document.SourcePath is null)
            return null;
        try
        {
            string identifier = ConfigurationLoader.ResolvePath(
                document.Configuration,
                document.Configuration.KeyFile);
            if (!File.Exists(identifier))
            {
                return new ConfigurationStudioKeyCompanion(
                    identifier,
                    null,
                    null,
                    $"The referenced key file does not exist: {Path.GetFileName(identifier)}",
                    LoadIssueIsWarning: true);
            }
            KeyContainer container = KeyFileLoader.Load(identifier);
            return new ConfigurationStudioKeyCompanion(
                identifier,
                KeyFileLoader.Serialize(container),
                ConfigurationDocument.ComputeFileHash(identifier),
                null,
                LoadIssueIsWarning: false);
        }
        catch (Exception exception) when (IsCompanionLoadFailure(exception))
        {
            return new ConfigurationStudioKeyCompanion(
                document.Configuration.KeyFile!,
                null,
                null,
                $"The referenced key file '{Path.GetFileName(document.Configuration.KeyFile)}' could not be opened: {DescribeFailure(exception)}",
                LoadIssueIsWarning: false);
        }
    }

    // Exception messages can contain sandbox paths or fragments of companion
    // content. Keep operator diagnostics useful without exposing either.
    private static string DescribeFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => "Access was denied.",
        YamlDotNet.Core.YamlException or FormatException or InvalidDataException => "The file format is invalid.",
        IOException => "The file could not be read.",
        _ => "The file reference or format is unsupported."
    };

    private static bool IsCompanionLoadFailure(Exception exception)
        => exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            InvalidDataException or
            FormatException or
            ArgumentException or
            NotSupportedException or
            YamlDotNet.Core.YamlException;
}
