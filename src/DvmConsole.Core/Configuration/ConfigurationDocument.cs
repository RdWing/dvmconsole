using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public sealed record UnknownConfigurationField(string Path, string Name)
{
    public string DisplayText => $"{Path}.{Name}";
}

public sealed class ConfigurationDocument
{
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "Temporary Phase 2 YAML allowlist: migrate this builder to a generated StaticContext while preserving unknown-field and read-only behavior.")]
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "Temporary Phase 2 YAML allowlist: migrate this builder to a generated StaticContext while preserving unknown-field and read-only behavior.")]
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(
            DefaultValuesHandling.OmitNull |
            DefaultValuesHandling.OmitEmptyCollections)
        .Build();
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification =
        "Temporary Phase 2 YAML allowlist: migrate this builder to a generated StaticContext while preserving unknown-field and read-only behavior.")]
    private static readonly ISerializer SchemaSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly YamlStream sourceTree;

    private ConfigurationDocument(
        ConsoleConfiguration configuration,
        string? sourcePath,
        string sourceText,
        YamlStream sourceTree,
        IReadOnlyList<UnknownConfigurationField> unknownFields,
        bool isReadOnly,
        string? readOnlyReason)
    {
        Configuration = configuration;
        SourcePath = sourcePath;
        SourceText = sourceText;
        SourceHash = ComputeHash(sourceText);
        this.sourceTree = sourceTree;
        UnknownFields = unknownFields;
        IsReadOnly = isReadOnly;
        ReadOnlyReason = readOnlyReason;
    }

    public ConsoleConfiguration Configuration { get; private set; }
    public string? SourcePath { get; private set; }
    public string SourceText { get; private set; }
    public string SourceHash { get; private set; }
    public YamlStream YamlNodeTree => sourceTree;
    public IReadOnlyList<UnknownConfigurationField> UnknownFields { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsReadOnly { get; }
    public string? ReadOnlyReason { get; }

    public static ConfigurationDocument Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string sourceText = File.ReadAllText(fullPath);
        return Parse(sourceText, fullPath);
    }

    public static ConfigurationDocument Parse(string sourceText, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var tree = new YamlStream();
        try
        {
            using var reader = new StringReader(sourceText);
            tree.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException exception) when (
            exception.Message.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleConfiguration duplicateConfiguration;
            try
            {
                duplicateConfiguration = Deserializer.Deserialize<ConsoleConfiguration>(sourceText)
                    ?? new ConsoleConfiguration();
            }
            catch (YamlDotNet.Core.YamlException)
            {
                duplicateConfiguration = new ConsoleConfiguration();
            }
            SetSourcePathAndNormalize(duplicateConfiguration, sourcePath);
            YamlStream fallbackTree = CreateCanonicalTree(duplicateConfiguration);
            return new ConfigurationDocument(
                duplicateConfiguration,
                duplicateConfiguration.SourcePath,
                sourceText,
                fallbackTree,
                [],
                isReadOnly: true,
                "Duplicate YAML mapping keys are shown read-only because their meaning is ambiguous.");
        }

        string? unsafeReason = FindUnsafeYamlReason(tree);
        ConsoleConfiguration configuration;
        try
        {
            configuration = Deserializer.Deserialize<ConsoleConfiguration>(sourceText)
                ?? throw new InvalidDataException("The codeplug file did not contain a configuration.");
        }
        catch (YamlDotNet.Core.YamlException) when (unsafeReason is not null)
        {
            configuration = new ConsoleConfiguration();
        }
        SetSourcePathAndNormalize(configuration, sourcePath);

        List<UnknownConfigurationField> unknown = CollectUnknownFields(tree, configuration);
        return new ConfigurationDocument(
            configuration,
            configuration.SourcePath,
            sourceText,
            tree,
            unknown,
            unsafeReason is not null,
            unsafeReason);
    }

    private static void SetSourcePathAndNormalize(ConsoleConfiguration configuration, string? sourcePath)
    {
        configuration.SourcePath = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetFullPath(sourcePath);
        ConfigurationNormalizer.Normalize(configuration);
    }

    private static YamlStream CreateCanonicalTree(ConsoleConfiguration configuration)
    {
        var tree = new YamlStream();
        using var reader = new StringReader(Serializer.Serialize(configuration));
        tree.Load(reader);
        return tree;
    }

    public static ConfigurationDocument CreateNew()
    {
        const string empty = "systems: []\nzones: []\ngroups: []\n";
        ConfigurationDocument document = Parse(empty);
        document.IsDirty = true;
        return document;
    }

    public void MarkDirty() => IsDirty = true;

    public void RemoveWebStreamAuthorization()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(
                ReadOnlyReason ?? "This YAML document cannot be safely rewritten.");
        }

        foreach (WebStreamConfiguration stream in Configuration.Zones.SelectMany(zone => zone.WebStreams))
        {
            stream.AuthUsername = null;
            stream.AuthPassword = null;
        }

        if (sourceTree.Documents.Count > 0 &&
            sourceTree.Documents[0].RootNode is YamlMappingNode root &&
            TryGetMappingValue(root, "zones", out YamlSequenceNode? zones) &&
            zones is not null)
        {
            foreach (YamlMappingNode zone in zones.Children.OfType<YamlMappingNode>())
            {
                if (!TryGetMappingValue(zone, "web_streams", out YamlSequenceNode? streams) ||
                    streams is null)
                    continue;
                foreach (YamlMappingNode stream in streams.Children.OfType<YamlMappingNode>())
                {
                    RemoveMappingEntry(stream, "authUsername");
                    RemoveMappingEntry(stream, "authPassword");
                }
            }
        }

        IsDirty = true;
    }

    public IReadOnlyList<ConfigurationValidationIssue> Validate()
        => ConfigurationValidator.ValidateDetailed(Configuration);

    public string Serialize()
    {
        if (IsReadOnly)
            throw new InvalidOperationException(ReadOnlyReason ?? "This YAML document cannot be safely rewritten.");

        string canonicalText = Serializer.Serialize(Configuration);
        var canonicalTree = new YamlStream();
        using (var reader = new StringReader(canonicalText))
            canonicalTree.Load(reader);

        if (sourceTree.Documents.Count > 0 && canonicalTree.Documents.Count > 0)
            MergeUnknownNodes(sourceTree.Documents[0].RootNode, canonicalTree.Documents[0].RootNode);

        using var writer = new StringWriter();
        canonicalTree.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    public string SerializeSanitized()
    {
        ConsoleConfiguration sanitized = Clone(Configuration);
        sanitized.KeyFile = null;
        foreach (SystemConfiguration system in sanitized.Systems)
        {
            system.Address = "redacted.invalid";
            system.Identity = string.Empty;
            system.Password = null;
            system.PresharedKey = null;
            system.KmfPresharedKey = null;
            system.PeerId = 0;
            system.Rid = string.Empty;
            system.AliasPath = string.Empty;
        }

        foreach (ZoneConfiguration zone in sanitized.Zones)
        {
            foreach (ChannelConfiguration channel in zone.Channels)
                channel.Tgid = "0";
            foreach (WebStreamConfiguration stream in zone.WebStreams)
            {
                stream.Url = "https://redacted.invalid/";
                stream.AuthUsername = null;
                stream.AuthPassword = null;
            }
        }

        return Serializer.Serialize(sanitized);
    }

    public void AcceptSaved(string path, string serializedText)
    {
        SourcePath = Path.GetFullPath(path);
        Configuration.SourcePath = SourcePath;
        SourceText = serializedText;
        SourceHash = ComputeHash(serializedText);
        IsDirty = false;
    }

    public static string ComputeFileHash(string path)
        => ComputeHash(File.ReadAllText(path));

    public static string ComputeHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ConsoleConfiguration Clone(ConsoleConfiguration configuration)
    {
        ConsoleConfiguration clone = Deserializer.Deserialize<ConsoleConfiguration>(Serializer.Serialize(configuration))
            ?? new ConsoleConfiguration();
        ConfigurationNormalizer.Normalize(clone);
        return clone;
    }

    private static string? FindUnsafeYamlReason(YamlStream stream)
    {
        if (stream.Documents.Count != 1)
            return "Configuration Studio can only rewrite YAML files containing one document.";

        foreach (YamlNode node in EnumerateNodes(stream.Documents[0].RootNode))
        {
            if (!node.Anchor.IsEmpty)
                return "YAML anchors and aliases are shown read-only because they cannot be retained safely.";
            if (!node.Tag.IsEmpty && !node.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
                return "Custom YAML tags are shown read-only because they cannot be retained safely.";
        }

        return null;
    }

    private static bool TryGetMappingValue<TNode>(
        YamlMappingNode mapping,
        string name,
        out TNode? value)
        where TNode : YamlNode
    {
        KeyValuePair<YamlNode, YamlNode> entry = mapping.Children.FirstOrDefault(candidate =>
            candidate.Key is YamlScalarNode scalar &&
            string.Equals(scalar.Value, name, StringComparison.Ordinal));
        value = entry.Value as TNode;
        return value is not null;
    }

    private static void RemoveMappingEntry(YamlMappingNode mapping, string name)
    {
        YamlNode? key = mapping.Children.Keys.FirstOrDefault(candidate =>
            candidate is YamlScalarNode scalar &&
            string.Equals(scalar.Value, name, StringComparison.Ordinal));
        if (key is not null)
            mapping.Children.Remove(key);
    }

    private static IEnumerable<YamlNode> EnumerateNodes(YamlNode node)
    {
        yield return node;
        if (node is YamlMappingNode mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
            {
                foreach (YamlNode child in EnumerateNodes(entry.Key))
                    yield return child;
                foreach (YamlNode child in EnumerateNodes(entry.Value))
                    yield return child;
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode item in sequence.Children)
                foreach (YamlNode child in EnumerateNodes(item))
                    yield return child;
        }
    }

    private static List<UnknownConfigurationField> CollectUnknownFields(
        YamlStream original,
        ConsoleConfiguration configuration)
    {
        string canonical = SchemaSerializer.Serialize(configuration);
        var canonicalTree = new YamlStream();
        using (var reader = new StringReader(canonical))
            canonicalTree.Load(reader);
        var unknown = new List<UnknownConfigurationField>();
        if (original.Documents.Count > 0 && canonicalTree.Documents.Count > 0)
            CollectUnknownNodes(original.Documents[0].RootNode, canonicalTree.Documents[0].RootNode, "$", unknown);
        return unknown;
    }

    private static void CollectUnknownNodes(
        YamlNode original,
        YamlNode canonical,
        string path,
        List<UnknownConfigurationField> unknown)
    {
        if (original is YamlMappingNode originalMap && canonical is YamlMappingNode canonicalMap)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in originalMap.Children)
            {
                string name = (entry.Key as YamlScalarNode)?.Value ?? entry.Key.ToString();
                if (!TryGetMappingValue(canonicalMap, name, out YamlNode? canonicalValue))
                    unknown.Add(new UnknownConfigurationField(path, name));
                else
                    CollectUnknownNodes(entry.Value, canonicalValue!, $"{path}.{name}", unknown);
            }
        }
        else if (original is YamlSequenceNode originalSequence && canonical is YamlSequenceNode canonicalSequence)
        {
            for (int index = 0; index < canonicalSequence.Children.Count; index++)
            {
                YamlNode? originalItem = FindCorrespondingSequenceItem(originalSequence, canonicalSequence.Children[index], index);
                if (originalItem is not null)
                    CollectUnknownNodes(originalItem, canonicalSequence.Children[index], $"{path}[{index}]", unknown);
            }
        }
    }

    private static void MergeUnknownNodes(YamlNode original, YamlNode canonical)
    {
        if (original is YamlMappingNode originalMap && canonical is YamlMappingNode canonicalMap)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in originalMap.Children)
            {
                string name = (entry.Key as YamlScalarNode)?.Value ?? entry.Key.ToString();
                if (TryGetMappingValue(canonicalMap, name, out YamlNode? canonicalValue))
                    MergeUnknownNodes(entry.Value, canonicalValue!);
                else
                    canonicalMap.Add(entry.Key, entry.Value);
            }
        }
        else if (original is YamlSequenceNode originalSequence && canonical is YamlSequenceNode canonicalSequence)
        {
            for (int index = 0; index < canonicalSequence.Children.Count; index++)
            {
                YamlNode? originalItem = FindCorrespondingSequenceItem(originalSequence, canonicalSequence.Children[index], index);
                if (originalItem is not null)
                    MergeUnknownNodes(originalItem, canonicalSequence.Children[index]);
            }
        }
    }

    private static YamlNode? FindCorrespondingSequenceItem(
        YamlSequenceNode originalSequence,
        YamlNode canonicalItem,
        int fallbackIndex)
    {
        if (canonicalItem is YamlMappingNode canonicalMap)
        {
            bool hasIdentity = false;
            string[][] identityKeys =
            [
                ["system", "tgid", "mode"],
                ["peerId", "rid"],
                ["url"],
                ["name"]
            ];
            foreach (string[] keys in identityKeys)
            {
                if (!TryBuildMappingIdentity(canonicalMap, keys, out string? identity))
                    continue;
                hasIdentity = true;
                List<YamlNode> matches = originalSequence.Children
                    .Where(item => item is YamlMappingNode originalMap &&
                        TryBuildMappingIdentity(originalMap, keys, out string? originalIdentity) &&
                        string.Equals(identity, originalIdentity, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 1)
                    return matches[0];
            }
            if (hasIdentity)
                return null;
        }

        return fallbackIndex < originalSequence.Children.Count
            ? originalSequence.Children[fallbackIndex]
            : null;
    }

    private static bool TryBuildMappingIdentity(
        YamlMappingNode mapping,
        IEnumerable<string> keys,
        out string? identity)
    {
        var values = new List<string>();
        foreach (string key in keys)
        {
            if (!TryGetMappingValue(mapping, key, out YamlNode? value) ||
                value is not YamlScalarNode scalar ||
                string.IsNullOrWhiteSpace(scalar.Value))
            {
                identity = null;
                return false;
            }
            values.Add(scalar.Value);
        }
        identity = string.Join("\u001F", values);
        return true;
    }

    private static bool TryGetMappingValue(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
