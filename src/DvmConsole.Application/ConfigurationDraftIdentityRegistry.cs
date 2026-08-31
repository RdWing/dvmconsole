using DvmConsole.Core.Configuration;

namespace DvmConsole.Application;

internal sealed record ConfigurationZoneIdentityLayout(
    Guid ZoneId,
    IReadOnlyList<Guid> ChannelIds,
    IReadOnlyList<Guid> StreamIds);

internal sealed record ConfigurationDraftIdentityLayout(
    IReadOnlyList<Guid> SystemIds,
    IReadOnlyList<ConfigurationZoneIdentityLayout> Zones,
    IReadOnlyList<Guid> GroupIds);

internal sealed class ConfigurationDraftIdentityRegistry
{
    private readonly Dictionary<SystemConfiguration, Guid> systemIds = [];
    private readonly Dictionary<Guid, SystemConfiguration> systems = [];
    private readonly Dictionary<ZoneConfiguration, Guid> zoneIds = [];
    private readonly Dictionary<Guid, ZoneConfiguration> zones = [];
    private readonly Dictionary<ChannelConfiguration, Guid> channelIds = [];
    private readonly Dictionary<Guid, ChannelConfiguration> channels = [];
    private readonly Dictionary<WebStreamConfiguration, Guid> streamIds = [];
    private readonly Dictionary<Guid, WebStreamConfiguration> streams = [];
    private readonly Dictionary<GroupConfiguration, Guid> groupIds = [];
    private readonly Dictionary<Guid, GroupConfiguration> groups = [];

    public void RegisterInitial(ConsoleConfiguration configuration)
    {
        Clear();
        Synchronize(configuration);
    }

    public void Synchronize(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (SystemConfiguration system in configuration.Systems)
            RegisterSystem(system);
        foreach (ZoneConfiguration zone in configuration.Zones)
        {
            RegisterZone(zone);
            foreach (ChannelConfiguration channel in zone.Channels)
                RegisterChannel(channel);
            foreach (WebStreamConfiguration stream in zone.WebStreams)
                RegisterStream(stream);
        }
        foreach (GroupConfiguration group in configuration.Groups)
            RegisterGroup(group);

        Prune(systemIds, systems, configuration.Systems);
        Prune(zoneIds, zones, configuration.Zones);
        Prune(channelIds, channels, configuration.Zones.SelectMany(zone => zone.Channels));
        Prune(streamIds, streams, configuration.Zones.SelectMany(zone => zone.WebStreams));
        Prune(groupIds, groups, configuration.Groups);
    }

    public Guid RegisterSystem(SystemConfiguration system) => Register(system, systemIds, systems);
    public Guid RegisterZone(ZoneConfiguration zone) => Register(zone, zoneIds, zones);
    public Guid RegisterChannel(ChannelConfiguration channel) => Register(channel, channelIds, channels);
    public Guid RegisterStream(WebStreamConfiguration stream) => Register(stream, streamIds, streams);
    public Guid RegisterGroup(GroupConfiguration group) => Register(group, groupIds, groups);

    public Guid GetSystemId(SystemConfiguration system) => systemIds[system];
    public Guid GetZoneId(ZoneConfiguration zone) => zoneIds[zone];
    public Guid GetChannelId(ChannelConfiguration channel) => channelIds[channel];
    public Guid GetStreamId(WebStreamConfiguration stream) => streamIds[stream];
    public Guid GetGroupId(GroupConfiguration group) => groupIds[group];

    public SystemConfiguration? FindSystem(Guid id) => systems.GetValueOrDefault(id);
    public ZoneConfiguration? FindZone(Guid id) => zones.GetValueOrDefault(id);
    public ChannelConfiguration? FindChannel(Guid id) => channels.GetValueOrDefault(id);
    public WebStreamConfiguration? FindStream(Guid id) => streams.GetValueOrDefault(id);
    public GroupConfiguration? FindGroup(Guid id) => groups.GetValueOrDefault(id);

    public ConfigurationDraftIdentityLayout Capture(ConsoleConfiguration configuration)
        => new(
            configuration.Systems.Select(GetSystemId).ToArray(),
            configuration.Zones.Select(zone => new ConfigurationZoneIdentityLayout(
                GetZoneId(zone),
                zone.Channels.Select(GetChannelId).ToArray(),
                zone.WebStreams.Select(GetStreamId).ToArray())).ToArray(),
            configuration.Groups.Select(GetGroupId).ToArray());

    public void Restore(ConsoleConfiguration configuration, ConfigurationDraftIdentityLayout layout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(layout);
        EnsureMatchingCount(configuration.Systems.Count, layout.SystemIds.Count, "systems");
        EnsureMatchingCount(configuration.Zones.Count, layout.Zones.Count, "zones");
        EnsureMatchingCount(configuration.Groups.Count, layout.GroupIds.Count, "groups");

        Clear();
        for (int index = 0; index < configuration.Systems.Count; index++)
            Register(configuration.Systems[index], layout.SystemIds[index], systemIds, systems);

        for (int zoneIndex = 0; zoneIndex < configuration.Zones.Count; zoneIndex++)
        {
            ZoneConfiguration zone = configuration.Zones[zoneIndex];
            ConfigurationZoneIdentityLayout zoneLayout = layout.Zones[zoneIndex];
            EnsureMatchingCount(zone.Channels.Count, zoneLayout.ChannelIds.Count, $"zones[{zoneIndex}].channels");
            EnsureMatchingCount(zone.WebStreams.Count, zoneLayout.StreamIds.Count, $"zones[{zoneIndex}].web_streams");
            Register(zone, zoneLayout.ZoneId, zoneIds, zones);
            for (int channelIndex = 0; channelIndex < zone.Channels.Count; channelIndex++)
                Register(zone.Channels[channelIndex], zoneLayout.ChannelIds[channelIndex], channelIds, channels);
            for (int streamIndex = 0; streamIndex < zone.WebStreams.Count; streamIndex++)
                Register(zone.WebStreams[streamIndex], zoneLayout.StreamIds[streamIndex], streamIds, streams);
        }

        for (int index = 0; index < configuration.Groups.Count; index++)
            Register(configuration.Groups[index], layout.GroupIds[index], groupIds, groups);
    }

    private void Clear()
    {
        systemIds.Clear();
        systems.Clear();
        zoneIds.Clear();
        zones.Clear();
        channelIds.Clear();
        channels.Clear();
        streamIds.Clear();
        streams.Clear();
        groupIds.Clear();
        groups.Clear();
    }

    private static Guid Register<T>(
        T item,
        Dictionary<T, Guid> ids,
        Dictionary<Guid, T> items)
        where T : class
    {
        if (ids.TryGetValue(item, out Guid existing))
            return existing;
        Guid id = Guid.NewGuid();
        Register(item, id, ids, items);
        return id;
    }

    private static void Register<T>(
        T item,
        Guid id,
        Dictionary<T, Guid> ids,
        Dictionary<Guid, T> items)
        where T : class
    {
        if (id == Guid.Empty || items.ContainsKey(id))
            throw new InvalidDataException("Configuration Studio encountered a duplicate or empty draft identity.");
        ids[item] = id;
        items[id] = item;
    }

    private static void EnsureMatchingCount(int configurationCount, int identityCount, string path)
    {
        if (configurationCount != identityCount)
        {
            throw new InvalidDataException(
                $"Configuration Studio could not restore draft identities for {path}: " +
                $"the configuration contains {configurationCount} item(s), but history contains {identityCount}.");
        }
    }

    private static void Prune<T>(
        Dictionary<T, Guid> ids,
        Dictionary<Guid, T> items,
        IEnumerable<T> currentItems)
        where T : class
    {
        HashSet<T> current = currentItems.ToHashSet();
        foreach (T removed in ids.Keys.Where(item => !current.Contains(item)).ToArray())
        {
            Guid id = ids[removed];
            ids.Remove(removed);
            items.Remove(id);
        }
    }
}
