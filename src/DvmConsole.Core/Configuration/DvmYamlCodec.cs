// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DvmConsole.Core.Configuration;

// YamlDotNet owns YAML syntax. This codec owns the finite DVM Console schema so
// configuration loading and saving remain visible to the AOT compiler.
internal static class DvmYamlCodec
{
    public static YamlStream ParseStream(string yaml, string documentName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        YamlResourceGuard.ValidateSource(yaml, documentName);

        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            throw new InvalidDataException($"The {documentName} did not contain a YAML document.");
        YamlResourceGuard.ValidateTree(stream, documentName);
        return stream;
    }

    public static ConsoleConfiguration ParseConfiguration(string yaml)
        => ReadConfiguration(ParseStream(yaml, "codeplug file"));

    public static ConsoleConfiguration ReadConfiguration(YamlStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        YamlMappingNode root = RequireMapping(Root(stream, "codeplug file"), "$", "codeplug file");
        var reader = new MappingReader(root, "$");

        return new ConsoleConfiguration
        {
            KeyFile = reader.OptionalString("keyFile"),
            Systems = reader.Objects("systems", ReadSystem),
            Zones = reader.Objects("zones", ReadZone),
            Groups = reader.Objects("groups", ReadGroup),
            LegacyPatchGroups = reader.Objects("patchGroups", ReadGroup),
            PatchSourceIdPassthrough = reader.Boolean("patchSourceIdPassthrough", defaultValue: false)
        };
    }

    public static YamlStream CreateConfigurationTree(
        ConsoleConfiguration configuration,
        bool includeEmptyCollections = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = new YamlMappingNode();
        AddOptionalString(root, "keyFile", configuration.KeyFile);
        AddObjects(root, "systems", configuration.Systems, WriteSystem, includeEmptyCollections);
        AddObjects(
            root,
            "zones",
            configuration.Zones,
            zone => WriteZone(zone, includeEmptyCollections),
            includeEmptyCollections);
        AddObjects(root, "groups", configuration.Groups, WriteGroup, includeEmptyCollections);
        AddObjects(root, "patchGroups", configuration.LegacyPatchGroups, WriteGroup, includeEmptyCollections);
        Add(root, "patchSourceIdPassthrough", Boolean(configuration.PatchSourceIdPassthrough));
        return Stream(root);
    }

    public static string SerializeConfiguration(
        ConsoleConfiguration configuration,
        bool includeEmptyCollections = false)
        => Save(CreateConfigurationTree(configuration, includeEmptyCollections));

    public static ConsoleConfiguration CloneConfiguration(ConsoleConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ConsoleConfiguration
        {
            KeyFile = source.KeyFile,
            Systems = source.Systems.Select(CloneSystem).ToList(),
            Zones = source.Zones.Select(CloneZone).ToList(),
            Groups = source.Groups.Select(CloneGroup).ToList(),
            LegacyPatchGroups = source.LegacyPatchGroups.Select(CloneGroup).ToList(),
            PatchSourceIdPassthrough = source.PatchSourceIdPassthrough,
            SourcePath = source.SourcePath
        };
    }

    public static List<RadioAlias> ParseAliases(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (string.IsNullOrWhiteSpace(yaml))
            return [];
        YamlNode root = Root(ParseStream(yaml, "alias file"), "alias file");
        return ReadObjects(root, "$", ReadAlias);
    }

    public static string SerializeAliases(IEnumerable<RadioAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        return Save(Stream(Sequence(aliases, WriteAlias)));
    }

    public static KeyContainer ParseKeys(string yaml)
    {
        YamlMappingNode root = RequireMapping(
            Root(ParseStream(yaml, "key file"), "key file"),
            "$",
            "key file");
        var reader = new MappingReader(root, "$");
        return new KeyContainer { Keys = reader.Objects("keys", ReadKey) };
    }

    public static string SerializeKeys(KeyContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var root = new YamlMappingNode();
        AddObjects(root, "keys", container.Keys, WriteKey, includeEmpty: false);
        return Save(Stream(root));
    }

    private static SystemConfiguration ReadSystem(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            Identity = reader.String("identity", string.Empty),
            Address = reader.String("address", string.Empty),
            Port = reader.Int32("port", 0),
            Password = reader.OptionalString("password"),
            PresharedKey = reader.OptionalString("presharedKey"),
            KmfPresharedKey = reader.OptionalString("kmfPresharedKey"),
            Encrypted = reader.Boolean("encrypted", defaultValue: false),
            TransportEncryptionMode = reader.String("transportEncryptionMode", "auto"),
            PeerId = reader.UInt32("peerId", 0),
            Rid = reader.String("rid", string.Empty),
            AliasPath = reader.String("aliasPath", "./alias.yml")
        };

    private static ZoneConfiguration ReadZone(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            TabColor = reader.OptionalString("tabColor"),
            TabTextColor = reader.OptionalString("tabTextColor"),
            Channels = reader.Objects("channels", ReadChannel),
            WebStreams = reader.Objects("web_streams", ReadWebStream)
        };

    private static GroupConfiguration ReadGroup(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            Type = reader.String("type", "patch")
        };

    private static ChannelConfiguration ReadChannel(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            System = reader.String("system", string.Empty),
            Tgid = reader.String("tgid", string.Empty),
            Slot = reader.Int32("slot", 1),
            Algo = reader.String("algo", "none"),
            KeyId = reader.OptionalString("keyId"),
            Mode = reader.String("mode", "p25"),
            ResourceColor = reader.OptionalString("resourceColor"),
            RxOnly = reader.Boolean("rx_only", defaultValue: false),
            SelectableEncryption = reader.Boolean("selectable_encryption", defaultValue: false),
            CardSize = reader.String("card_size", "normal")
        };

    private static WebStreamConfiguration ReadWebStream(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            Url = reader.String("url", string.Empty),
            AuthUsername = reader.OptionalString("authUsername"),
            AuthPassword = reader.OptionalString("authPassword"),
            IdleColor = reader.OptionalString("idleColor")
        };

    private static RadioAlias ReadAlias(MappingReader reader)
        => new()
        {
            Alias = reader.String("alias", string.Empty),
            Rid = reader.UInt32("rid", 0)
        };

    private static KeyEntry ReadKey(MappingReader reader)
        => new()
        {
            Name = reader.String("name", string.Empty),
            System = reader.String("system", string.Empty),
            Protocol = reader.String("protocol", "p25"),
            KeyId = reader.UInt16("keyId", 0),
            AlgId = reader.Int32("algId", 0),
            Key = reader.String("key", string.Empty)
        };

    private static YamlMappingNode WriteSystem(SystemConfiguration value)
    {
        var map = new YamlMappingNode();
        Add(map, "name", String(value.Name));
        Add(map, "identity", String(value.Identity));
        Add(map, "address", String(value.Address));
        Add(map, "port", Integer(value.Port));
        AddOptionalString(map, "password", value.Password);
        AddOptionalString(map, "presharedKey", value.PresharedKey);
        AddOptionalString(map, "kmfPresharedKey", value.KmfPresharedKey);
        Add(map, "encrypted", Boolean(value.Encrypted));
        Add(map, "transportEncryptionMode", String(value.TransportEncryptionMode));
        Add(map, "peerId", Integer(value.PeerId));
        Add(map, "rid", String(value.Rid));
        Add(map, "aliasPath", String(value.AliasPath));
        return map;
    }

    private static YamlMappingNode WriteZone(
        ZoneConfiguration value,
        bool includeEmptyCollections = false)
    {
        var map = new YamlMappingNode();
        Add(map, "name", String(value.Name));
        AddOptionalString(map, "tabColor", value.TabColor);
        AddOptionalString(map, "tabTextColor", value.TabTextColor);
        AddObjects(map, "channels", value.Channels, WriteChannel, includeEmptyCollections);
        AddObjects(map, "web_streams", value.WebStreams, WriteWebStream, includeEmptyCollections);
        return map;
    }

    private static YamlMappingNode WriteGroup(GroupConfiguration value)
    {
        var map = new YamlMappingNode();
        Add(map, "name", String(value.Name));
        Add(map, "type", String(value.Type));
        return map;
    }

    private static YamlMappingNode WriteChannel(ChannelConfiguration value)
    {
        var map = new YamlMappingNode();
        Add(map, "name", String(value.Name));
        Add(map, "system", String(value.System));
        Add(map, "tgid", String(value.Tgid));
        Add(map, "slot", Integer(value.Slot));
        Add(map, "algo", String(value.Algo));
        AddOptionalString(map, "keyId", value.KeyId);
        Add(map, "mode", String(value.Mode));
        AddOptionalString(map, "resourceColor", value.ResourceColor);
        Add(map, "rx_only", Boolean(value.RxOnly));
        Add(map, "selectable_encryption", Boolean(value.SelectableEncryption));
        Add(map, "card_size", String(value.CardSize));
        return map;
    }

    private static YamlMappingNode WriteWebStream(WebStreamConfiguration value)
    {
        var map = new YamlMappingNode();
        Add(map, "name", String(value.Name));
        Add(map, "url", String(value.Url));
        AddOptionalString(map, "authUsername", value.AuthUsername);
        AddOptionalString(map, "authPassword", value.AuthPassword);
        AddOptionalString(map, "idleColor", value.IdleColor);
        return map;
    }

    private static YamlMappingNode WriteAlias(RadioAlias value)
    {
        var map = new YamlMappingNode();
        Add(map, "alias", String(value.Alias));
        Add(map, "rid", Integer(value.Rid));
        return map;
    }

    private static YamlMappingNode WriteKey(KeyEntry value)
    {
        var map = new YamlMappingNode();
        AddOptionalString(map, "name", value.Name);
        AddOptionalString(map, "system", value.System);
        Add(map, "protocol", String(value.Protocol));
        Add(map, "keyId", HexInteger(value.KeyId));
        Add(map, "algId", HexInteger(value.AlgId));
        Add(map, "key", String(value.Key));
        return map;
    }

    private static SystemConfiguration CloneSystem(SystemConfiguration value)
        => new()
        {
            Name = value.Name,
            Identity = value.Identity,
            Address = value.Address,
            Port = value.Port,
            Password = value.Password,
            PresharedKey = value.PresharedKey,
            KmfPresharedKey = value.KmfPresharedKey,
            Encrypted = value.Encrypted,
            TransportEncryptionMode = value.TransportEncryptionMode,
            PeerId = value.PeerId,
            Rid = value.Rid,
            AliasPath = value.AliasPath,
            RidAlias = value.RidAlias.Select(alias => new RadioAlias { Alias = alias.Alias, Rid = alias.Rid }).ToList()
        };

    private static ZoneConfiguration CloneZone(ZoneConfiguration value)
        => new()
        {
            Name = value.Name,
            TabColor = value.TabColor,
            TabTextColor = value.TabTextColor,
            Channels = value.Channels.Select(CloneChannel).ToList(),
            WebStreams = value.WebStreams.Select(CloneWebStream).ToList()
        };

    private static GroupConfiguration CloneGroup(GroupConfiguration value)
        => new() { Name = value.Name, Type = value.Type };

    private static ChannelConfiguration CloneChannel(ChannelConfiguration value)
        => new()
        {
            Name = value.Name,
            System = value.System,
            Tgid = value.Tgid,
            Slot = value.Slot,
            Algo = value.Algo,
            KeyId = value.KeyId,
            Mode = value.Mode,
            ResourceColor = value.ResourceColor,
            RxOnly = value.RxOnly,
            SelectableEncryption = value.SelectableEncryption,
            CardSize = value.CardSize
        };

    private static WebStreamConfiguration CloneWebStream(WebStreamConfiguration value)
        => new()
        {
            Name = value.Name,
            Url = value.Url,
            AuthUsername = value.AuthUsername,
            AuthPassword = value.AuthPassword,
            IdleColor = value.IdleColor
        };

    private static YamlNode Root(YamlStream stream, string documentName)
    {
        if (stream.Documents.Count == 0)
            throw new InvalidDataException($"The {documentName} did not contain a YAML document.");
        return stream.Documents[0].RootNode;
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string path, string documentName)
        => node as YamlMappingNode
           ?? throw new InvalidDataException($"The {documentName} value at '{path}' must be a mapping.");

    private static List<T> ReadObjects<T>(
        YamlNode node,
        string path,
        Func<MappingReader, T> read)
    {
        if (IsNull(node))
            return [];
        if (node is not YamlSequenceNode sequence)
            throw new InvalidDataException($"The YAML value at '{path}' must be a sequence.");

        var result = new List<T>(sequence.Children.Count);
        for (int index = 0; index < sequence.Children.Count; index++)
        {
            string itemPath = $"{path}[{index}]";
            YamlMappingNode map = RequireMapping(sequence.Children[index], itemPath, "YAML");
            result.Add(read(new MappingReader(map, itemPath)));
        }
        return result;
    }

    private static YamlStream Stream(YamlNode root)
        => new(new YamlDocument(root));

    private static YamlSequenceNode Sequence<T>(IEnumerable<T>? values, Func<T, YamlNode> write)
    {
        var sequence = new YamlSequenceNode();
        foreach (T value in values ?? [])
            sequence.Add(write(value));
        return sequence;
    }

    private static void AddObjects<T>(
        YamlMappingNode map,
        string name,
        IEnumerable<T>? values,
        Func<T, YamlNode> write,
        bool includeEmpty)
    {
        YamlSequenceNode sequence = Sequence(values, write);
        if (includeEmpty || sequence.Children.Count > 0)
            Add(map, name, sequence);
    }

    private static void AddOptionalString(YamlMappingNode map, string name, string? value)
    {
        if (value is not null)
            Add(map, name, String(value));
    }

    private static void Add(YamlMappingNode map, string name, YamlNode value)
        => map.Add(new YamlScalarNode(name), value);

    private static YamlScalarNode String(string? value)
    {
        value ??= string.Empty;
        return new YamlScalarNode(value)
        {
            Style = NeedsQuotes(value) ? ScalarStyle.SingleQuoted : ScalarStyle.Any
        };
    }

    private static YamlScalarNode Integer<T>(T value) where T : IFormattable
        => new(value.ToString(null, CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain };

    private static YamlScalarNode HexInteger<T>(T value) where T : IFormattable
        => new($"0x{value.ToString("X", CultureInfo.InvariantCulture)}") { Style = ScalarStyle.Plain };

    private static YamlScalarNode Boolean(bool value)
        => new(value ? "true" : "false") { Style = ScalarStyle.Plain };

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0 || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return true;
        if (value.IndexOfAny(['\r', '\n', '\t']) >= 0)
            return true;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0], StringComparison.Ordinal))
            return true;
        if (value.Contains(": ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal))
            return true;
        if (value is "~" ||
            value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            bool.TryParse(value, out _) ||
            TryParseInteger(value, out _) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }
        return false;
    }

    private static bool IsNull(YamlNode node)
        => node is YamlScalarNode scalar &&
           (scalar.Value is null ||
            scalar.Style is ScalarStyle.Plain or ScalarStyle.Any &&
            (scalar.Value == "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase)));

    private static string Scalar(YamlNode node, string path)
        => node is YamlScalarNode scalar && !IsNull(scalar)
            ? scalar.Value ?? string.Empty
            : throw new InvalidDataException($"The YAML value at '{path}' must be a scalar.");

    private static long Integer(YamlNode node, string path)
    {
        string value = Scalar(node, path);
        if (!TryParseInteger(value, out long result))
            throw new InvalidDataException($"The YAML value at '{path}' must be an integer.");
        return result;
    }

    private static bool TryParseInteger(string value, out long result)
    {
        string normalized = value.Replace("_", string.Empty, StringComparison.Ordinal);
        bool negative = normalized.StartsWith('-');
        string unsigned = normalized.TrimStart('+', '-');
        int numberBase = unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : unsigned.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8
            : 10;
        if (numberBase != 10)
            unsigned = unsigned[2..];
        if (unsigned.Length == 0)
        {
            result = 0;
            return false;
        }

        long magnitude;
        if (numberBase == 8)
        {
            magnitude = 0;
            foreach (char digit in unsigned)
            {
                if (digit is < '0' or > '7' || magnitude > (long.MaxValue - (digit - '0')) / 8)
                {
                    result = 0;
                    return false;
                }
                magnitude = magnitude * 8 + digit - '0';
            }
        }
        else if (!long.TryParse(
                     unsigned,
                     numberBase == 10 ? NumberStyles.None : NumberStyles.AllowHexSpecifier,
                     CultureInfo.InvariantCulture,
                     out magnitude))
        {
            result = 0;
            return false;
        }

        result = negative ? -magnitude : magnitude;
        return true;
    }

    private static string Save(YamlStream stream)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private sealed class MappingReader(YamlMappingNode map, string path)
    {
        public string? OptionalString(string name)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return null;
            return Scalar(node, ChildPath(name));
        }

        public string String(string name, string defaultValue)
            => !TryValue(name, out YamlNode? node) || node is null || IsNull(node)
                ? defaultValue
                : Scalar(node, ChildPath(name));

        public bool Boolean(string name, bool defaultValue)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return defaultValue;
            string value = Scalar(node, ChildPath(name));
            return bool.TryParse(value, out bool result)
                ? result
                : throw new InvalidDataException($"The YAML value at '{ChildPath(name)}' must be true or false.");
        }

        public int Int32(string name, int defaultValue)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return defaultValue;
            long value = Integer(node, ChildPath(name));
            return value is >= int.MinValue and <= int.MaxValue
                ? (int)value
                : throw new InvalidDataException($"The YAML value at '{ChildPath(name)}' is outside the Int32 range.");
        }

        public uint UInt32(string name, uint defaultValue)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return defaultValue;
            long value = Integer(node, ChildPath(name));
            return value is >= uint.MinValue and <= uint.MaxValue
                ? (uint)value
                : throw new InvalidDataException($"The YAML value at '{ChildPath(name)}' is outside the UInt32 range.");
        }

        public ushort UInt16(string name, ushort defaultValue)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return defaultValue;
            long value = Integer(node, ChildPath(name));
            return value is >= ushort.MinValue and <= ushort.MaxValue
                ? (ushort)value
                : throw new InvalidDataException($"The YAML value at '{ChildPath(name)}' is outside the UInt16 range.");
        }

        public List<T> Objects<T>(string name, Func<MappingReader, T> read)
        {
            if (!TryValue(name, out YamlNode? node) || node is null || IsNull(node))
                return [];
            return ReadObjects(node, ChildPath(name), read);
        }

        private bool TryValue(string name, out YamlNode? value)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> entry in map.Children)
            {
                if (entry.Key is YamlScalarNode key && string.Equals(key.Value, name, StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private string ChildPath(string name) => $"{path}.{name}";
    }
}
