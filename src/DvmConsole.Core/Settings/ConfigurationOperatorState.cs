namespace DvmConsole.Core.Settings;

// Configuration-scoped operator choices. The desktop runtime continues to use
// the established UserSettings properties as its working copy during Phase 1;
// this envelope is the authoritative persisted form for managed
// configurations and is keyed by ConfigurationId rather than by a path.
public sealed class ConfigurationOperatorState
{
    public Dictionary<string, WidgetPositionSetting> ChannelWidgetPositions { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ReceiveEnabledChannelKeys { get; set; } = [];
    public List<string> TransmitSelectedChannelKeys { get; set; } = [];
    public Dictionary<string, double> ChannelVolumes { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ChannelStereoBalances { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ChannelOutputDeviceIds { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WebStreamOutputDeviceIds { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> WebStreamVolumes { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RecordingEnabledChannelKeys { get; set; } = [];
    public Dictionary<string, List<uint>> RecordingIgnoredSubscriberIds { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public CodeplugGroupState GroupState { get; set; } = new();
    public CodeplugStudioState StudioState { get; set; } = new();
    public bool RetainPatchStateOnStartup { get; set; }
    public bool RestoreSelectedChannelsOnStartup { get; set; } = true;
    public List<string> SelectedWebStreams { get; set; } = [];
    public Dictionary<string, bool> TransmitEncryptionStates { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSelectedSystemName { get; set; }
    public string? LastSelectedChannelKey { get; set; }

    public ConfigurationOperatorState Clone(bool includeWebStreamAuthorization = true)
        => new()
        {
            ChannelWidgetPositions = CloneWidgetPositions(ChannelWidgetPositions),
            ReceiveEnabledChannelKeys = [.. ReceiveEnabledChannelKeys ?? []],
            TransmitSelectedChannelKeys = [.. TransmitSelectedChannelKeys ?? []],
            ChannelVolumes = CloneDictionary(ChannelVolumes),
            ChannelStereoBalances = CloneDictionary(ChannelStereoBalances),
            ChannelOutputDeviceIds = CloneDictionary(ChannelOutputDeviceIds),
            WebStreamOutputDeviceIds = CloneDictionary(WebStreamOutputDeviceIds),
            WebStreamVolumes = CloneDictionary(WebStreamVolumes),
            RecordingEnabledChannelKeys = [.. RecordingEnabledChannelKeys ?? []],
            RecordingIgnoredSubscriberIds = (RecordingIgnoredSubscriberIds ?? [])
                .ToDictionary(
                    entry => entry.Key,
                    entry => (entry.Value ?? []).ToList(),
                    StringComparer.OrdinalIgnoreCase),
            GroupState = (GroupState ?? new CodeplugGroupState()).Clone(),
            StudioState = (StudioState ?? new CodeplugStudioState()).Clone(),
            RetainPatchStateOnStartup = RetainPatchStateOnStartup,
            RestoreSelectedChannelsOnStartup = RestoreSelectedChannelsOnStartup,
            SelectedWebStreams = includeWebStreamAuthorization
                ? [.. SelectedWebStreams ?? []]
                : [],
            TransmitEncryptionStates = CloneDictionary(TransmitEncryptionStates),
            LastSelectedSystemName = LastSelectedSystemName,
            LastSelectedChannelKey = LastSelectedChannelKey
        };

    private static Dictionary<string, WidgetPositionSetting> CloneWidgetPositions(
        Dictionary<string, WidgetPositionSetting>? source)
        => (source ?? [])
            .Where(entry => entry.Value is not null)
            .ToDictionary(
                entry => entry.Key,
                entry => new WidgetPositionSetting
                {
                    X = entry.Value.X,
                    Y = entry.Value.Y
                },
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, TValue> CloneDictionary<TValue>(
        Dictionary<string, TValue>? source)
        => new(source ?? [], StringComparer.OrdinalIgnoreCase);
}

public static class ConfigurationOperatorStateStore
{
    public static void Activate(
        UserSettings settings,
        string configurationId,
        string runtimePath,
        bool allowLegacyAttribution)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string id = NormalizeId(configurationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        settings.ConfigurationOperatorStates ??=
            new Dictionary<string, ConfigurationOperatorState>(StringComparer.OrdinalIgnoreCase);

        if (!settings.ConfigurationOperatorStates.TryGetValue(id, out ConfigurationOperatorState? state))
        {
            state = allowLegacyAttribution && !settings.LegacyConfigurationOperatorStateMigrated
                ? CaptureWorkingState(settings, runtimePath)
                : new ConfigurationOperatorState();
            settings.ConfigurationOperatorStates[id] = state;
            // The first managed activation is the only safe migration point.
            // When it was an explicit command-line/import choice, mark the
            // ambiguous legacy globals consumed without assigning them.
            settings.LegacyConfigurationOperatorStateMigrated = true;
        }

        ApplyWorkingState(settings, runtimePath, state);
        settings.ActiveConfigurationOperatorStateId = id;
    }

    public static void CaptureActive(
        UserSettings settings,
        string configurationId,
        string runtimePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string id = NormalizeId(configurationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        settings.ConfigurationOperatorStates ??=
            new Dictionary<string, ConfigurationOperatorState>(StringComparer.OrdinalIgnoreCase);
        settings.ConfigurationOperatorStates[id] = CaptureWorkingState(settings, runtimePath);
        settings.ActiveConfigurationOperatorStateId = id;
    }

    public static void Copy(
        UserSettings settings,
        string sourceConfigurationId,
        string destinationConfigurationId,
        bool includeWebStreamAuthorization)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string sourceId = NormalizeId(sourceConfigurationId);
        string destinationId = NormalizeId(destinationConfigurationId);
        settings.ConfigurationOperatorStates ??=
            new Dictionary<string, ConfigurationOperatorState>(StringComparer.OrdinalIgnoreCase);
        if (!settings.ConfigurationOperatorStates.TryGetValue(sourceId, out ConfigurationOperatorState? source))
            return;
        settings.ConfigurationOperatorStates[destinationId] =
            source.Clone(includeWebStreamAuthorization);
    }

    public static void UpdateDocumentState(
        UserSettings settings,
        string configurationId,
        CodeplugGroupState groupState,
        CodeplugStudioState studioState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(groupState);
        ArgumentNullException.ThrowIfNull(studioState);
        string id = NormalizeId(configurationId);
        settings.ConfigurationOperatorStates ??=
            new Dictionary<string, ConfigurationOperatorState>(StringComparer.OrdinalIgnoreCase);
        if (!settings.ConfigurationOperatorStates.TryGetValue(id, out ConfigurationOperatorState? state))
        {
            state = new ConfigurationOperatorState();
            settings.ConfigurationOperatorStates[id] = state;
        }
        state.GroupState = groupState.Clone();
        state.StudioState = studioState.Clone();
    }

    private static ConfigurationOperatorState CaptureWorkingState(
        UserSettings settings,
        string runtimePath)
        => new()
        {
            ChannelWidgetPositions = CloneWidgetPositions(settings.ChannelWidgetPositions),
            ReceiveEnabledChannelKeys = [.. settings.ReceiveEnabledChannelKeys ?? []],
            TransmitSelectedChannelKeys = [.. settings.TransmitSelectedChannelKeys ?? []],
            ChannelVolumes = CloneDictionary(settings.ChannelVolumes),
            ChannelStereoBalances = CloneDictionary(settings.ChannelStereoBalances),
            ChannelOutputDeviceIds = CloneDictionary(settings.ChannelOutputDeviceIds),
            WebStreamOutputDeviceIds = CloneDictionary(settings.WebStreamOutputDeviceIds),
            WebStreamVolumes = CloneDictionary(settings.WebStreamVolumes),
            RecordingEnabledChannelKeys = [.. settings.RecordingEnabledChannelKeys ?? []],
            RecordingIgnoredSubscriberIds = (settings.RecordingIgnoredSubscriberIds ?? [])
                .ToDictionary(
                    entry => entry.Key,
                    entry => (entry.Value ?? []).ToList(),
                    StringComparer.OrdinalIgnoreCase),
            GroupState = CodeplugGroupStateStore.GetOrMigrate(settings, runtimePath).Clone(),
            StudioState = CodeplugStudioStateStore.Get(settings, runtimePath).Clone(),
            RetainPatchStateOnStartup = settings.RetainPatchStateOnStartup,
            RestoreSelectedChannelsOnStartup = settings.RestoreSelectedChannelsOnStartup,
            SelectedWebStreams = [.. settings.SelectedWebStreams ?? []],
            TransmitEncryptionStates = CloneDictionary(settings.TransmitEncryptionStates),
            LastSelectedSystemName = settings.LastSelectedSystemName,
            LastSelectedChannelKey = settings.LastSelectedChannelKey
        };

    private static void ApplyWorkingState(
        UserSettings settings,
        string runtimePath,
        ConfigurationOperatorState source)
    {
        ConfigurationOperatorState state = source.Clone();
        settings.ChannelWidgetPositions = state.ChannelWidgetPositions;
        settings.ReceiveEnabledChannelKeys = state.ReceiveEnabledChannelKeys;
        settings.TransmitSelectedChannelKeys = state.TransmitSelectedChannelKeys;
        settings.ChannelVolumes = state.ChannelVolumes;
        settings.ChannelStereoBalances = state.ChannelStereoBalances;
        settings.ChannelOutputDeviceIds = state.ChannelOutputDeviceIds;
        settings.WebStreamOutputDeviceIds = state.WebStreamOutputDeviceIds;
        settings.WebStreamVolumes = state.WebStreamVolumes;
        settings.RecordingEnabledChannelKeys = state.RecordingEnabledChannelKeys;
        settings.RecordingIgnoredSubscriberIds = state.RecordingIgnoredSubscriberIds;
        settings.RetainPatchStateOnStartup = state.RetainPatchStateOnStartup;
        settings.RestoreSelectedChannelsOnStartup = state.RestoreSelectedChannelsOnStartup;
        settings.SelectedWebStreams = state.SelectedWebStreams;
        settings.TransmitEncryptionStates = state.TransmitEncryptionStates;
        settings.LastSelectedSystemName = state.LastSelectedSystemName;
        settings.LastSelectedChannelKey = state.LastSelectedChannelKey;

        string pathKey = CodeplugGroupStateStore.NormalizePath(runtimePath);
        settings.CodeplugGroupStates ??=
            new Dictionary<string, CodeplugGroupState>(StringComparer.OrdinalIgnoreCase);
        settings.CodeplugStudioStates ??=
            new Dictionary<string, CodeplugStudioState>(StringComparer.OrdinalIgnoreCase);
        settings.CodeplugGroupStates[pathKey] = state.GroupState;
        settings.CodeplugStudioStates[pathKey] = state.StudioState;
        settings.LegacyPatchGroupStateMigrated = true;
    }

    private static string NormalizeId(string value)
    {
        if (!Guid.TryParse(value?.Trim(), out Guid id))
            throw new ArgumentException("A valid configuration ID is required.", nameof(value));
        return id.ToString("N");
    }

    private static Dictionary<string, WidgetPositionSetting> CloneWidgetPositions(
        Dictionary<string, WidgetPositionSetting>? source)
        => (source ?? [])
            .ToDictionary(
                entry => entry.Key,
                entry => new WidgetPositionSetting
                {
                    X = entry.Value.X,
                    Y = entry.Value.Y
                },
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, TValue> CloneDictionary<TValue>(
        Dictionary<string, TValue>? source)
        => new(source ?? [], StringComparer.OrdinalIgnoreCase);
}
