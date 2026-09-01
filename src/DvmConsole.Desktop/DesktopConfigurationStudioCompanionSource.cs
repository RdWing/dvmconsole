using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

/// <summary>
/// Resolves the temporary materialized desktop workspace used by Configuration Studio.
/// Mobile hosts can supply sandbox or document-handle backed companion sources instead.
/// </summary>
internal sealed class DesktopConfigurationStudioCompanionSource : IConfigurationStudioCompanionSource
{
    public string CreateWebStreamAuthorizationIdentity(
        string hostDocumentIdentity,
        WebStreamConfiguration stream)
        => WebStreamSelectionIdentity.Create(hostDocumentIdentity, stream);

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
                        warnings.Add($"Alias file for system '{system.Name}' does not exist yet: {identifier}");
                        continue;
                    }
                    List<RadioAlias> loaded = AliasFileLoader.Load(identifier);
                    aliases[identifier] = new ConfigurationStudioAliasCompanion(
                        identifier,
                        [system.AliasPath],
                        AliasFileLoader.Serialize(loaded),
                        ConfigurationDocument.ComputeFileHash(identifier));
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
                {
                    errors.Add($"Alias file for system '{system.Name}' could not be opened: {exception.Message}");
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
                    $"The referenced key file does not exist: {identifier}",
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
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            return new ConfigurationStudioKeyCompanion(
                document.Configuration.KeyFile!,
                null,
                null,
                $"The referenced key file could not be opened: {exception.Message}",
                LoadIssueIsWarning: false);
        }
    }
}
