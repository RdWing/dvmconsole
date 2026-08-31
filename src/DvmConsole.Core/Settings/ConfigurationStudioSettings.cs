namespace DvmConsole.Core.Settings;

public sealed class CodeplugStudioState
{
    public Dictionary<string, string> ZoneSystemAssignments { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Neo operator policy. It intentionally remains outside interoperable YAML.
    public List<string> CallPrioritySystemNames { get; set; } = [];

    public CodeplugStudioState Clone()
        => new()
        {
            ZoneSystemAssignments = new Dictionary<string, string>(
                ZoneSystemAssignments,
                StringComparer.OrdinalIgnoreCase),
            CallPrioritySystemNames = CallPrioritySystemNames.ToList()
        };
}

public static class CodeplugStudioStateStore
{
    public static CodeplugStudioState Get(UserSettings settings, string? codeplugPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(codeplugPath))
            return new CodeplugStudioState();

        string key = CodeplugGroupStateStore.NormalizePath(codeplugPath);
        settings.CodeplugStudioStates ??= new Dictionary<string, CodeplugStudioState>(StringComparer.OrdinalIgnoreCase);
        if (settings.CodeplugStudioStates.TryGetValue(key, out CodeplugStudioState? existing))
            return existing;

        var created = new CodeplugStudioState();
        settings.CodeplugStudioStates[key] = created;
        return created;
    }

    public static CodeplugStudioState CopyForSaveAs(
        UserSettings settings,
        string sourcePath,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CodeplugStudioState source = Get(settings, sourcePath);
        string destinationKey = CodeplugGroupStateStore.NormalizePath(destinationPath);
        CodeplugStudioState copy = source.Clone();
        settings.CodeplugStudioStates[destinationKey] = copy;
        return copy;
    }
}
