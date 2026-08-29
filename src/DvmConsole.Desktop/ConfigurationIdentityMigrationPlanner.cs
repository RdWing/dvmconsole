using DvmConsole.Core.Configuration;

namespace DvmConsole.Desktop;

internal sealed record OriginalSystemIdentity(Guid Id, string Name);
internal sealed record OriginalChannelIdentity(
    Guid Id,
    string System,
    string Name,
    string DestinationId,
    string Mode);
internal sealed record OriginalStreamIdentity(
    Guid Id,
    WebStreamConfiguration Configuration,
    string Name);
internal sealed record OriginalGroupIdentity(Guid Id, string Name);

internal sealed record ChannelIdentityMigration(
    OriginalChannelIdentity Original,
    ChannelConfiguration? Current)
{
    public string OriginalSettingsKey => $"{Original.System}\u001F{Original.Name}";
    public string? CurrentSettingsKey => Current is null ? null : $"{Current.System}\u001F{Current.Name}";
}

internal sealed record StreamIdentityMigration(
    OriginalStreamIdentity Original,
    WebStreamConfiguration? Current);

internal sealed class ConfigurationIdentityMigrationPlanner
{
    private readonly ConfigurationDraftIdentityRegistry identities;
    private OriginalSystemIdentity[] originalSystems = [];
    private OriginalChannelIdentity[] originalChannels = [];
    private OriginalStreamIdentity[] originalStreams = [];
    private OriginalGroupIdentity[] originalGroups = [];

    public ConfigurationIdentityMigrationPlanner(
        ConsoleConfiguration configuration,
        ConfigurationDraftIdentityRegistry identities)
    {
        this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
        ResetBaseline(configuration);
    }

    public IReadOnlyList<OriginalSystemIdentity> OriginalSystems => originalSystems;
    public IReadOnlyList<OriginalChannelIdentity> OriginalChannels => originalChannels;

    public void ResetBaseline(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        originalSystems = configuration.Systems
            .Select(system => new OriginalSystemIdentity(identities.GetSystemId(system), system.Name))
            .ToArray();
        originalChannels = configuration.Zones
            .SelectMany(zone => zone.Channels)
            .Select(channel => new OriginalChannelIdentity(
                identities.GetChannelId(channel),
                channel.System,
                channel.Name,
                channel.Tgid,
                channel.Mode))
            .ToArray();
        originalStreams = configuration.Zones
            .SelectMany(zone => zone.WebStreams)
            .Select(stream => new OriginalStreamIdentity(
                identities.GetStreamId(stream),
                CloneStream(stream),
                stream.Name))
            .ToArray();
        originalGroups = configuration.Groups
            .Select(group => new OriginalGroupIdentity(identities.GetGroupId(group), group.Name))
            .ToArray();
    }

    public Dictionary<string, string> BuildSystemRenames()
        => originalSystems
            .Select(original => (Original: original, Current: identities.FindSystem(original.Id)))
            .Where(pair => pair.Current is not null && !string.Equals(
                pair.Original.Name,
                pair.Current.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Original.Name,
                pair => pair.Current!.Name,
                StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BuildDeletedSystems()
        => originalSystems
            .Where(original => identities.FindSystem(original.Id) is null)
            .Select(original => original.Name)
            .ToArray();

    public (Dictionary<string, string> Renames, IReadOnlyList<string> Deleted) BuildGroupMigrations()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        foreach (OriginalGroupIdentity original in originalGroups)
        {
            GroupConfiguration? current = identities.FindGroup(original.Id);
            if (current is null)
                deleted.Add(original.Name);
            else if (!string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                renames[original.Name] = current.Name;
        }
        return (renames, deleted);
    }

    public IReadOnlyList<ChannelIdentityMigration> BuildChannelMigrations()
        => originalChannels
            .Select(original => new ChannelIdentityMigration(original, identities.FindChannel(original.Id)))
            .Where(migration => !string.Equals(
                migration.OriginalSettingsKey,
                migration.CurrentSettingsKey,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<StreamIdentityMigration> BuildStreamMigrations(bool includeUnchanged = false)
        => originalStreams
            .Select(original => new StreamIdentityMigration(original, identities.FindStream(original.Id)))
            .Where(migration => includeUnchanged || migration.Current is null || !string.Equals(
                migration.Original.Name,
                migration.Current.Name,
                StringComparison.OrdinalIgnoreCase) || !string.Equals(
                migration.Original.Configuration.Url,
                migration.Current.Url,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public SystemConfiguration? FindCurrentSystem(OriginalSystemIdentity original)
        => identities.FindSystem(original.Id);

    public ChannelConfiguration? FindCurrentChannel(OriginalChannelIdentity original)
        => identities.FindChannel(original.Id);

    private static WebStreamConfiguration CloneStream(WebStreamConfiguration source)
        => new()
        {
            Name = source.Name,
            Url = source.Url,
            AuthUsername = source.AuthUsername,
            AuthPassword = source.AuthPassword,
            IdleColor = source.IdleColor
        };
}
