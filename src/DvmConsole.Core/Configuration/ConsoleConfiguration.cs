using YamlDotNet.Serialization;

namespace DvmConsole.Core.Configuration;

// Cross-platform representation of the existing dvmconsole codeplug format.
// The YAML names intentionally match the legacy configuration contract.
public sealed class ConsoleConfiguration
{
    [YamlMember(Alias = "keyFile")]
    public string? KeyFile { get; set; }

    public List<SystemConfiguration> Systems { get; set; } = [];

    public List<ZoneConfiguration> Zones { get; set; } = [];

    public List<GroupConfiguration> Groups { get; set; } = [];

    [YamlMember(Alias = "patchGroups")]
    public List<GroupConfiguration> LegacyPatchGroups { get; set; } = [];

    public bool PatchSourceIdPassthrough { get; set; }

    [YamlIgnore]
    public string? SourcePath { get; internal set; }

    // Resolves current and legacy group keys using the same merge semantics as
    // the WPF codeplug loader. The current key wins when names overlap.
    public IEnumerable<GroupConfiguration> EffectiveGroups()
        => ResolveGroups(Groups, LegacyPatchGroups);

    public void NormalizeGroups()
    {
        Groups = ResolveGroups(Groups, LegacyPatchGroups).ToList();
        LegacyPatchGroups = [];
    }

    private static IEnumerable<GroupConfiguration> ResolveGroups(
        IEnumerable<GroupConfiguration>? current,
        IEnumerable<GroupConfiguration>? legacy)
    {
        return (current ?? Enumerable.Empty<GroupConfiguration>())
            .Concat(legacy ?? Enumerable.Empty<GroupConfiguration>())
            .Where(group => group is not null)
            .GroupBy(group => (group.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                GroupConfiguration first = group.First();
                return new GroupConfiguration
                {
                    Name = (first.Name ?? string.Empty).Trim(),
                    Type = string.IsNullOrWhiteSpace(first.Type)
                        ? "patch"
                        : first.Type.Trim().ToLowerInvariant()
                };
            });
    }
}

public sealed class SystemConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Password { get; set; }
    public string? PresharedKey { get; set; }
    // Optional KMF key used only by FNE KMM peer-encrypted key responses. It
    // is intentionally separate from the FNE transport preshared key.
    public string? KmfPresharedKey { get; set; }
    public bool Encrypted { get; set; }
    public uint PeerId { get; set; }
    public string Rid { get; set; } = string.Empty;
    public string AliasPath { get; set; } = "./alias.yml";

    [YamlIgnore]
    public List<RadioAlias> RidAlias { get; set; } = [];
}

public sealed class ZoneConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string? TabColor { get; set; }
    public string? TabTextColor { get; set; }
    public List<ChannelConfiguration> Channels { get; set; } = [];

    [YamlMember(Alias = "web_streams", ApplyNamingConventions = false)]
    public List<WebStreamConfiguration> WebStreams { get; set; } = [];
}

public sealed class GroupConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "patch";

    public bool IsPatchGroup()
        => !string.Equals(Type?.Trim(), "multiselect", StringComparison.OrdinalIgnoreCase);

    public bool IsMultiselectGroup()
        => string.Equals(Type?.Trim(), "multiselect", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChannelConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public string Tgid { get; set; } = string.Empty;
    public int Slot { get; set; } = 1;
    public string Algo { get; set; } = "none";
    public string? KeyId { get; set; }
    public string Mode { get; set; } = "p25";
    public string? ResourceColor { get; set; }

    [YamlMember(Alias = "rx_only", ApplyNamingConventions = false)]
    public bool RxOnly { get; set; }

    [YamlMember(Alias = "selectable_encryption", ApplyNamingConventions = false)]
    public bool SelectableEncryption { get; set; }

    [YamlMember(Alias = "card_size", ApplyNamingConventions = false)]
    public string CardSize { get; set; } = "normal";
}

public sealed class WebStreamConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? AuthUsername { get; set; }
    public string? AuthPassword { get; set; }
    public string? IdleColor { get; set; }
}

public sealed class RadioAlias
{
    public string Alias { get; set; } = string.Empty;
    public uint Rid { get; set; }
}
