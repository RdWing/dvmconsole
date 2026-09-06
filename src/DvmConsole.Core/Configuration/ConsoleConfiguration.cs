// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;

namespace DvmConsole.Core.Configuration;

// Cross-platform representation of the existing dvmconsole codeplug format.
// The YAML names intentionally match the legacy configuration contract.
public sealed class ConsoleConfiguration
{
    public string? KeyFile { get; set; }

    public List<SystemConfiguration> Systems { get; set; } = [];

    public List<ZoneConfiguration> Zones { get; set; } = [];

    public List<GroupConfiguration> Groups { get; set; } = [];

    public List<GroupConfiguration> LegacyPatchGroups { get; set; } = [];

    public bool PatchSourceIdPassthrough { get; set; }

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

public sealed class SystemConfiguration : INotifyPropertyChanged
{
    private List<RadioAlias> ridAlias = [];
    private string name = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => name;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(name, next, StringComparison.Ordinal))
                return;
            name = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public string Identity { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Password { get; set; }
    public string? PresharedKey { get; set; }
    // Optional KMF key used only by FNE KMM peer-encrypted key responses. It
    // is intentionally separate from the FNE transport preshared key.
    public string? KmfPresharedKey { get; set; }
    public bool Encrypted { get; set; }
    // FNE transport framing compatibility: auto, ecb, or cbc. Auto starts
    // with ECB and locks to whichever mode returns a valid FNE response.
    public string TransportEncryptionMode { get; set; } = "auto";
    public uint PeerId { get; set; }
    public string Rid { get; set; } = string.Empty;
    public string AliasPath { get; set; } = "./alias.yml";

    public List<RadioAlias> RidAlias
    {
        get => ridAlias;
        set
        {
            ridAlias = value ?? [];
            AliasIndex = new RadioAliasIndex(ridAlias);
        }
    }

    public RadioAliasIndex AliasIndex { get; internal set; } = RadioAliasIndex.Empty;

    public override string ToString() => Name;
}

public sealed class ZoneConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string? TabColor { get; set; }
    public string? TabTextColor { get; set; }
    public List<ChannelConfiguration> Channels { get; set; } = [];

    public List<WebStreamConfiguration> WebStreams { get; set; } = [];
}

public sealed class GroupConfiguration : INotifyPropertyChanged
{
    private string name = string.Empty;
    private string type = "patch";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => name;
        set
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
                return;
            name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public string Type
    {
        get => type;
        set
        {
            if (string.Equals(type, value, StringComparison.Ordinal))
                return;
            type = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type)));
        }
    }

    public bool IsPatchGroup()
        => string.IsNullOrWhiteSpace(Type) ||
           string.Equals(Type.Trim(), "patch", StringComparison.OrdinalIgnoreCase);

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

    public bool RxOnly { get; set; }

    public bool SelectableEncryption { get; set; }

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
