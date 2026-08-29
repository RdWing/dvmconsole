namespace DvmConsole.Core.Settings;

// Persisted patch member identity. Runtime patch routing converts this safe
// settings DTO into a validated `PatchMemberAddress`.
public sealed class PatchMemberSetting
{
    public string SystemName { get; set; } = string.Empty;
    public uint DestinationId { get; set; }
    public string? ChannelName { get; set; }
}

public sealed class CodeplugGroupState
{
    public Dictionary<string, List<PatchMemberSetting>> Memberships { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> OneWayModes { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> EnabledStates { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public CodeplugGroupState Clone()
        => new()
        {
            Memberships = Memberships.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Select(member => new PatchMemberSetting
                {
                    SystemName = member.SystemName,
                    DestinationId = member.DestinationId,
                    ChannelName = member.ChannelName
                }).ToList(),
                StringComparer.OrdinalIgnoreCase),
            OneWayModes = new Dictionary<string, bool>(OneWayModes, StringComparer.OrdinalIgnoreCase),
            EnabledStates = new Dictionary<string, bool>(EnabledStates, StringComparer.OrdinalIgnoreCase)
        };
}

public static class CodeplugGroupStateStore
{
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path.Trim());
    }

    public static CodeplugGroupState GetOrMigrate(UserSettings settings, string? codeplugPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(codeplugPath))
            return new CodeplugGroupState();

        string key = NormalizePath(codeplugPath);
        settings.CodeplugGroupStates ??= new Dictionary<string, CodeplugGroupState>(StringComparer.OrdinalIgnoreCase);
        if (settings.CodeplugGroupStates.TryGetValue(key, out CodeplugGroupState? existing))
            return existing;

        if (settings.LegacyPatchGroupStateMigrated)
        {
            var empty = new CodeplugGroupState();
            settings.CodeplugGroupStates[key] = empty;
            return empty;
        }

        var migrated = new CodeplugGroupState
        {
            Memberships = CloneMemberships(settings.PatchGroupMemberships),
            OneWayModes = new Dictionary<string, bool>(settings.PatchGroupModes, StringComparer.OrdinalIgnoreCase),
            EnabledStates = new Dictionary<string, bool>(settings.PatchGroupEnabledStates, StringComparer.OrdinalIgnoreCase)
        };
        settings.CodeplugGroupStates[key] = migrated;
        settings.LegacyPatchGroupStateMigrated = true;

        // Legacy values remain until a later cleanup migration. Keeping them in
        // the same atomic settings write means a failed write cannot lose the
        // only copy of an operator's patch configuration.
        return migrated;
    }

    public static CodeplugGroupState CopyForSaveAs(UserSettings settings, string sourcePath, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CodeplugGroupState source = GetOrMigrate(settings, sourcePath);
        string destinationKey = NormalizePath(destinationPath);
        CodeplugGroupState copy = source.Clone();
        settings.CodeplugGroupStates[destinationKey] = copy;
        return copy;
    }

    private static Dictionary<string, List<PatchMemberSetting>> CloneMemberships(
        Dictionary<string, List<PatchMemberSetting>>? memberships)
        => (memberships ?? new Dictionary<string, List<PatchMemberSetting>>())
            .ToDictionary(
                entry => entry.Key,
                entry => (entry.Value ?? []).Select(member => new PatchMemberSetting
                {
                    SystemName = member.SystemName,
                    DestinationId = member.DestinationId,
                    ChannelName = member.ChannelName
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);
}
