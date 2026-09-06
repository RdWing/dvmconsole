// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;
using DvmConsole.Core.IO;

namespace DvmConsole.Core.Configuration;

public sealed record UnknownConfigurationField(string Path, string Name)
{
    public string DisplayText => $"{Path}.{Name}";
}

public sealed class ConfigurationDocument
{
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
        string sourceText = BoundedResourceReader.ReadUtf8File(
            fullPath,
            ManagedResourceLimits.ConfigurationYamlBytes,
            "Configuration YAML");
        return Parse(sourceText, fullPath);
    }

    public static ConfigurationDocument Parse(string sourceText, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        YamlResourceGuard.ValidateSource(sourceText, "configuration YAML");
        var tree = new YamlStream();
        try
        {
            using var reader = new StringReader(sourceText);
            tree.Load(reader);
            YamlResourceGuard.ValidateTree(tree, "configuration YAML");
        }
        catch (YamlDotNet.Core.YamlException exception) when (
            exception.Message.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            var duplicateConfiguration = new ConsoleConfiguration();
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
            configuration = DvmYamlCodec.ReadConfiguration(tree);
        }
        catch (InvalidDataException) when (unsafeReason is not null)
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
        => DvmYamlCodec.CreateConfigurationTree(configuration);

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

        YamlStream canonicalTree = DvmYamlCodec.CreateConfigurationTree(Configuration);

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
        sanitized.PatchSourceIdPassthrough = false;
        var systemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < sanitized.Systems.Count; index++)
        {
            SystemConfiguration system = sanitized.Systems[index];
            string replacement = $"System {index + 1}";
            systemNames[system.Name] = replacement;
            system.Name = replacement;
            system.Address = "redacted.invalid";
            system.Port = 1;
            system.Identity = $"console-{index + 1}";
            system.Password = null;
            system.PresharedKey = null;
            system.KmfPresharedKey = null;
            system.Encrypted = false;
            system.TransportEncryptionMode = "auto";
            system.PeerId = (uint)(index + 1);
            system.Rid = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            system.AliasPath = string.Empty;
            system.RidAlias = [];
        }

        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int nextIdentifier = 10_001;
        int nextChannel = 1;
        int nextStream = 1;
        for (int zoneIndex = 0; zoneIndex < sanitized.Zones.Count; zoneIndex++)
        {
            ZoneConfiguration zone = sanitized.Zones[zoneIndex];
            zone.Name = $"Zone {zoneIndex + 1}";
            zone.TabColor = null;
            zone.TabTextColor = null;
            foreach (ChannelConfiguration channel in zone.Channels)
            {
                channel.Name = $"Channel {nextChannel++}";
                if (systemNames.TryGetValue(channel.System, out string? systemName))
                    channel.System = systemName;
                if (!identifiers.TryGetValue(channel.Tgid, out string? identifier))
                {
                    identifier = nextIdentifier++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    identifiers[channel.Tgid] = identifier;
                }
                channel.Tgid = identifier;
                channel.Algo = "none";
                channel.KeyId = null;
                channel.SelectableEncryption = false;
                channel.ResourceColor = null;
            }
            foreach (WebStreamConfiguration stream in zone.WebStreams)
            {
                stream.Name = $"Stream {nextStream++}";
                stream.Url = "https://redacted.invalid/";
                stream.AuthUsername = null;
                stream.AuthPassword = null;
                stream.IdleColor = null;
            }
        }

        for (int index = 0; index < sanitized.Groups.Count; index++)
            sanitized.Groups[index].Name = $"Group {index + 1}";
        for (int index = 0; index < sanitized.LegacyPatchGroups.Count; index++)
            sanitized.LegacyPatchGroups[index].Name = $"Legacy Group {index + 1}";

        return DvmYamlCodec.SerializeConfiguration(sanitized);
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
        => ComputeHash(BoundedResourceReader.ReadUtf8File(
            path,
            ManagedResourceLimits.ConfigurationYamlBytes,
            "Configuration YAML"));

    public static string ComputeHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ConsoleConfiguration Clone(ConsoleConfiguration configuration)
    {
        ConsoleConfiguration clone = DvmYamlCodec.CloneConfiguration(configuration);
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
        YamlStream canonicalTree = DvmYamlCodec.CreateConfigurationTree(
            configuration,
            includeEmptyCollections: true);
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
            var identities = new SequenceIdentityIndex(originalSequence);
            for (int index = 0; index < canonicalSequence.Children.Count; index++)
            {
                YamlNode? originalItem = identities.Find(canonicalSequence.Children[index], index);
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
            var identities = new SequenceIdentityIndex(originalSequence);
            for (int index = 0; index < canonicalSequence.Children.Count; index++)
            {
                YamlNode? originalItem = identities.Find(canonicalSequence.Children[index], index);
                if (originalItem is not null)
                    MergeUnknownNodes(originalItem, canonicalSequence.Children[index]);
            }
        }
    }

    // A sequence is matched repeatedly during serialization and unknown-field
    // discovery. Build each identity lookup once, keeping ambiguous identities
    // unresolved so the next, more specific discriminator can still be tried.
    private sealed class SequenceIdentityIndex(YamlSequenceNode original)
    {
        private static readonly string[][] IdentityKeys =
        [
            ["system", "tgid", "mode"],
            ["peerId", "rid"],
            ["url"],
            ["name"]
        ];
        private readonly Dictionary<string, YamlNode?>?[] indexes =
            new Dictionary<string, YamlNode?>?[IdentityKeys.Length];

        public YamlNode? Find(YamlNode canonicalItem, int fallbackIndex)
        {
            if (canonicalItem is YamlMappingNode canonicalMap)
            {
                bool hasIdentity = false;
                for (int index = 0; index < IdentityKeys.Length; index++)
                {
                    if (!TryBuildMappingIdentity(canonicalMap, IdentityKeys[index], out string? identity))
                        continue;
                    hasIdentity = true;
                    Dictionary<string, YamlNode?> lookup = indexes[index] ??= Build(IdentityKeys[index]);
                    if (lookup.TryGetValue(identity!, out YamlNode? match) && match is not null)
                        return match;
                }
                if (hasIdentity)
                    return null;
            }
            return fallbackIndex < original.Children.Count ? original.Children[fallbackIndex] : null;
        }

        private Dictionary<string, YamlNode?> Build(string[] keys)
        {
            var lookup = new Dictionary<string, YamlNode?>(StringComparer.Ordinal);
            foreach (YamlNode item in original.Children)
            {
                if (item is not YamlMappingNode mapping ||
                    !TryBuildMappingIdentity(mapping, keys, out string? identity))
                    continue;
                if (!lookup.TryAdd(identity!, item))
                    lookup[identity!] = null;
            }
            return lookup;
        }
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
