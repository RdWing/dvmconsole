// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal interface IConfigurationStudioEntityEditSession
{
    ConsoleConfiguration Configuration { get; }
    bool CanEditEntities { get; }
    SystemConfiguration? SelectedSystemForEdit { get; }
    ZoneConfiguration? SelectedZoneForEdit { get; }
    ChannelConfiguration? SelectedChannelForEdit { get; }
    ConfigurationStreamRow? SelectedStreamForEdit { get; }
    GroupConfiguration? SelectedGroupForEdit { get; }
    string GetZoneSystemName(ZoneConfiguration zone);
    void SetZoneSystemName(ZoneConfiguration zone, string systemName);
    void MutateEntities(Action mutation);
    void SelectSystem(SystemConfiguration? system);
    void SelectZone(ZoneConfiguration? zone);
    void SelectChannel(ChannelConfiguration? channel);
    void SelectStream(WebStreamConfiguration? stream);
    void SelectGroup(GroupConfiguration? group);
    void SelectSectionForEdit(ConfigurationStudioSection section);
}

/// <summary>
/// Owns system, zone, channel, web-stream, and group creation, duplication,
/// removal, placement, and ordering
/// while the public Studio view model remains the binding-compatible facade.
/// </summary>
internal sealed class ConfigurationStudioEntityEditController
{
    private readonly IConfigurationStudioEntityEditSession session;

    public ConfigurationStudioEntityEditController(IConfigurationStudioEntityEditSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void AddSystem()
    {
        if (!session.CanEditEntities)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        var system = new SystemConfiguration
        {
            Name = UniqueName("New System", configuration.Systems.Select(candidate => candidate.Name)),
            Address = "127.0.0.1",
            Port = 62031,
            Identity = "DVM Console",
            TransportEncryptionMode = "auto",
            AliasPath = string.Empty
        };
        var zone = new ZoneConfiguration
        {
            Name = UniqueName("New Zone", configuration.Zones.Select(candidate => candidate.Name))
        };
        session.MutateEntities(() =>
        {
            configuration.Systems.Add(system);
            configuration.Zones.Add(zone);
            session.SetZoneSystemName(zone, system.Name);
        });
        session.SelectSystem(system);
        session.SelectZone(zone);
    }

    public void DuplicateSystem()
    {
        if (!session.CanEditEntities || session.SelectedSystemForEdit is not { } source)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        var copy = new SystemConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", configuration.Systems.Select(candidate => candidate.Name)),
            Identity = source.Identity,
            Address = source.Address,
            Port = source.Port,
            Password = source.Password,
            PresharedKey = source.PresharedKey,
            KmfPresharedKey = source.KmfPresharedKey,
            Encrypted = source.Encrypted,
            TransportEncryptionMode = source.TransportEncryptionMode,
            PeerId = source.PeerId,
            Rid = source.Rid,
            AliasPath = source.AliasPath
        };
        session.MutateEntities(() => configuration.Systems.Add(copy));
        session.SelectSystem(copy);
    }

    public void DeleteSystem(SystemConfiguration? system = null)
    {
        system ??= session.SelectedSystemForEdit;
        if (!session.CanEditEntities || system is null || !session.Configuration.Systems.Contains(system))
            return;
        session.MutateEntities(() => session.Configuration.Systems.Remove(system));
    }

    public void AddZone()
    {
        if (!session.CanEditEntities)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        var zone = new ZoneConfiguration
        {
            Name = UniqueName("New Zone", configuration.Zones.Select(candidate => candidate.Name))
        };
        session.MutateEntities(() =>
        {
            configuration.Zones.Add(zone);
            session.SetZoneSystemName(zone,
                session.SelectedSystemForEdit?.Name ?? configuration.Systems.FirstOrDefault()?.Name ?? string.Empty);
        });
        session.SelectZone(zone);
    }

    public void DuplicateZone()
    {
        if (!session.CanEditEntities || session.SelectedZoneForEdit is not { } source)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        var usedChannelNames = configuration.Zones
            .SelectMany(zone => zone.Channels)
            .GroupBy(channel => channel.System ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(channel => channel.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var usedStreamNames = configuration.Zones
            .SelectMany(zone => zone.WebStreams)
            .Select(stream => stream.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copiedChannels = new List<ChannelConfiguration>(source.Channels.Count);
        foreach (ChannelConfiguration sourceChannel in source.Channels)
        {
            ChannelConfiguration channel = CloneChannel(sourceChannel);
            string systemName = channel.System ?? string.Empty;
            if (!usedChannelNames.TryGetValue(systemName, out HashSet<string>? names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedChannelNames[systemName] = names;
            }
            channel.Name = UniqueName($"{sourceChannel.Name} Copy", names);
            names.Add(channel.Name);
            copiedChannels.Add(channel);
        }

        var copiedStreams = new List<WebStreamConfiguration>(source.WebStreams.Count);
        foreach (WebStreamConfiguration sourceStream in source.WebStreams)
        {
            WebStreamConfiguration stream = CloneStream(sourceStream);
            stream.Name = UniqueName($"{sourceStream.Name} Copy", usedStreamNames);
            usedStreamNames.Add(stream.Name);
            copiedStreams.Add(stream);
        }

        var copy = new ZoneConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", configuration.Zones.Select(zone => zone.Name)),
            TabColor = source.TabColor,
            TabTextColor = source.TabTextColor,
            Channels = copiedChannels,
            WebStreams = copiedStreams
        };
        session.MutateEntities(() =>
        {
            configuration.Zones.Add(copy);
            session.SetZoneSystemName(copy, session.GetZoneSystemName(source));
        });
        session.SelectZone(copy);
    }

    public void DeleteZone()
    {
        if (!session.CanEditEntities || session.SelectedZoneForEdit is not { } zone)
            return;
        session.MutateEntities(() => session.Configuration.Zones.Remove(zone));
    }

    public void AddChannel()
    {
        if (!session.CanEditEntities)
            return;
        if (session.SelectedZoneForEdit is null)
            AddZone();
        if (session.SelectedZoneForEdit is not { } zone)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        var channel = new ChannelConfiguration
        {
            Name = UniqueName(
                "New Channel",
                configuration.Zones.SelectMany(candidate => candidate.Channels).Select(candidate => candidate.Name)),
            System = session.GetZoneSystemName(zone),
            Tgid = "1",
            CardSize = "normal"
        };
        session.MutateEntities(() => zone.Channels.Add(channel));
        session.SelectChannel(channel);
    }

    public void AddChannelToSelectedSystem()
    {
        if (!session.CanEditEntities || session.SelectedSystemForEdit is not { } system)
            return;

        ConsoleConfiguration configuration = session.Configuration;
        string systemName = system.Name;
        ZoneConfiguration? zone = session.SelectedZoneForEdit is { } selectedZone &&
                                  string.Equals(session.GetZoneSystemName(selectedZone), systemName, StringComparison.OrdinalIgnoreCase)
            ? selectedZone
            : configuration.Zones.FirstOrDefault(candidate =>
                string.Equals(session.GetZoneSystemName(candidate), systemName, StringComparison.OrdinalIgnoreCase));
        bool addZone = zone is null;
        zone ??= new ZoneConfiguration
        {
            Name = UniqueName($"{systemName} Zone", configuration.Zones.Select(candidate => candidate.Name))
        };
        var channel = new ChannelConfiguration
        {
            Name = UniqueName(
                "New Channel",
                configuration.Zones.SelectMany(candidate => candidate.Channels).Select(candidate => candidate.Name)),
            System = systemName,
            Tgid = "1",
            CardSize = "normal"
        };

        session.MutateEntities(() =>
        {
            if (addZone)
            {
                configuration.Zones.Add(zone);
                session.SetZoneSystemName(zone, systemName);
            }
            zone.Channels.Add(channel);
        });
        session.SelectZone(zone);
        session.SelectChannel(channel);
        session.SelectSectionForEdit(ConfigurationStudioSection.Zones);
    }

    public void DuplicateChannel()
    {
        if (!session.CanEditEntities ||
            session.SelectedZoneForEdit is not { } zone ||
            session.SelectedChannelForEdit is not { } source)
        {
            return;
        }

        ChannelConfiguration copy = CloneChannel(source);
        copy.Name = UniqueName(
            $"{source.Name} Copy",
            session.Configuration.Zones.SelectMany(candidate => candidate.Channels).Select(candidate => candidate.Name));
        copy.System = session.GetZoneSystemName(zone);
        session.MutateEntities(() => zone.Channels.Add(copy));
        session.SelectChannel(copy);
    }

    public void DeleteChannel()
    {
        if (!session.CanEditEntities ||
            session.SelectedZoneForEdit is not { } zone ||
            session.SelectedChannelForEdit is not { } channel)
        {
            return;
        }
        session.MutateEntities(() => zone.Channels.Remove(channel));
    }

    public void MoveChannel(int offset)
    {
        if (!session.CanEditEntities ||
            session.SelectedZoneForEdit is not { } zone ||
            session.SelectedChannelForEdit is not { } channel)
        {
            return;
        }

        int index = zone.Channels.IndexOf(channel);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= zone.Channels.Count)
            return;
        session.MutateEntities(() =>
        {
            zone.Channels.RemoveAt(index);
            zone.Channels.Insert(destination, channel);
        });
    }

    public void SetChannelsRxOnly(IEnumerable<ChannelConfiguration> channels, bool rxOnly)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (!session.CanEditEntities || selected.Length == 0)
            return;
        session.MutateEntities(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.RxOnly = rxOnly;
        });
    }

    public void ApplySelectedCardSize(IEnumerable<ChannelConfiguration> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (!session.CanEditEntities || session.SelectedChannelForEdit is not { } source)
            return;
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (selected.Length == 0)
            return;
        session.MutateEntities(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.CardSize = source.CardSize;
        });
    }

    public void AddStream()
    {
        if (!session.CanEditEntities)
            return;
        ZoneConfiguration? zone = session.SelectedZoneForEdit ?? session.Configuration.Zones.FirstOrDefault();
        bool addZone = zone is null;
        zone ??= new ZoneConfiguration { Name = "New Zone" };

        var stream = new WebStreamConfiguration
        {
            Name = UniqueName(
                "New Stream",
                session.Configuration.Zones
                    .SelectMany(candidate => candidate.WebStreams)
                    .Select(candidate => candidate.Name)),
            Url = "https://example.invalid/stream"
        };
        session.MutateEntities(() =>
        {
            if (addZone)
            {
                session.Configuration.Zones.Add(zone);
                session.SetZoneSystemName(zone, session.SelectedSystemForEdit?.Name ?? string.Empty);
            }
            zone.WebStreams.Add(stream);
        });
        session.SelectZone(zone);
        session.SelectStream(stream);
    }

    public void DeleteStream()
    {
        if (!session.CanEditEntities || session.SelectedStreamForEdit is not { } row)
            return;
        session.MutateEntities(() => row.Zone.WebStreams.Remove(row.Stream));
    }

    public void MoveSelectedStreamTo(ZoneConfiguration zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!session.CanEditEntities ||
            session.SelectedStreamForEdit is not { } row ||
            ReferenceEquals(row.Zone, zone) ||
            !session.Configuration.Zones.Contains(zone))
        {
            return;
        }

        WebStreamConfiguration stream = row.Stream;
        session.MutateEntities(() =>
        {
            row.Zone.WebStreams.Remove(stream);
            zone.WebStreams.Add(stream);
        });
        session.SelectStream(stream);
    }

    public void AddGroup()
    {
        if (!session.CanEditEntities)
            return;
        var group = new GroupConfiguration
        {
            Name = UniqueName(
                "New Group",
                session.Configuration.Groups.Select(candidate => candidate.Name)),
            Type = "patch"
        };
        session.MutateEntities(() => session.Configuration.Groups.Add(group));
        session.SelectGroup(group);
    }

    public void DeleteGroup()
    {
        if (!session.CanEditEntities || session.SelectedGroupForEdit is not { } group)
            return;
        session.MutateEntities(() => session.Configuration.Groups.Remove(group));
    }

    private static ChannelConfiguration CloneChannel(ChannelConfiguration source)
        => new()
        {
            Name = source.Name,
            System = source.System,
            Tgid = source.Tgid,
            Slot = source.Slot,
            Algo = source.Algo,
            KeyId = source.KeyId,
            Mode = source.Mode,
            ResourceColor = source.ResourceColor,
            RxOnly = source.RxOnly,
            SelectableEncryption = source.SelectableEncryption,
            CardSize = source.CardSize
        };

    private static WebStreamConfiguration CloneStream(WebStreamConfiguration source)
        => new()
        {
            Name = source.Name,
            Url = source.Url,
            AuthUsername = source.AuthUsername,
            AuthPassword = source.AuthPassword,
            IdleColor = source.IdleColor
        };

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }
}
