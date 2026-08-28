using Avalonia.Media;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

public enum ConfigurationStudioSection
{
    Overview,
    Systems,
    Zones,
    Streams,
    Groups,
    EncryptionKeys,
    Files
}

public sealed record ConfigurationStudioNavigationItem(
    ConfigurationStudioSection Section,
    string Label,
    string Description);

public sealed record ConfigurationStreamRow(ZoneConfiguration Zone, WebStreamConfiguration Stream)
{
    public string ZoneName => Zone.Name;
}

public sealed record ConfigurationAliasRow(string FilePath, RadioAlias Alias);

public sealed class ConfigurationChannelPreviewViewModel : INotifyPropertyChanged
{
    private double x;
    private double y;

    public ConfigurationChannelPreviewViewModel(
        ChannelConfiguration channel,
        double x,
        double y,
        double cardHeight)
    {
        Channel = channel;
        this.x = x;
        this.y = y;
        CardHeight = cardHeight;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ChannelConfiguration Channel { get; }
    public string Name => Channel.Name;
    public string SystemText => Channel.System;
    public string DestinationText => $"{Channel.Mode.ToUpperInvariant()}  •  {Channel.Tgid}";
    public string CardSizeText => string.Equals(Channel.CardSize, "normal", StringComparison.OrdinalIgnoreCase)
        ? "Normal"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Channel.CardSize ?? "normal");
    public double CardWidth => ChannelViewModel.ResolveCardWidth(Channel.CardSize);
    public double CardHeight { get; }
    public IBrush AccentBrush
    {
        get
        {
            try
            {
                return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(Channel.ResourceColor)
                    ? "#244E73"
                    : Channel.ResourceColor));
            }
            catch (FormatException)
            {
                return new SolidColorBrush(Color.Parse("#244E73"));
            }
        }
    }
    public double X
    {
        get => x;
        set => SetField(ref x, Math.Max(0, value));
    }
    public double Y
    {
        get => y;
        set => SetField(ref y, Math.Max(0, value));
    }

    private void SetField(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.01)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ConfigurationStudioViewModel : INotifyPropertyChanged
{
    private sealed record SystemIdentity(string Name, uint PeerId, string Rid, string Address, int Index);
    private sealed record ChannelIdentity(
        string System,
        string Name,
        string DestinationId,
        string Mode,
        int ZoneIndex,
        int ChannelIndex);
    private sealed record StreamIdentity(
        WebStreamConfiguration Configuration,
        string Name,
        string Url,
        int ZoneIndex,
        int StreamIndex);

    private readonly MainWindowViewModel runtimeViewModel;
    private readonly UserSettingsStore settingsStore;
    private readonly Stack<string> undo = [];
    private readonly Stack<string> redo = [];
    private readonly Dictionary<string, WidgetPositionSetting> originalWidgetPositions;
    private readonly Dictionary<ChannelConfiguration, WidgetPositionSetting> draftWidgetPositions = [];
    private readonly SystemIdentity[] originalSystems;
    private readonly ChannelIdentity[] originalChannels;
    private readonly StreamIdentity[] originalStreams;
    private readonly string[] originalGroupNames;
    private ConfigurationDocument document;
    private string lastSnapshot;
    private ConfigurationStudioNavigationItem selectedNavigation;
    private SystemConfiguration? selectedSystem;
    private ZoneConfiguration? selectedZone;
    private ChannelConfiguration? selectedChannel;
    private ConfigurationStreamRow? selectedStream;
    private GroupConfiguration? selectedGroup;
    private KeyEntry? selectedKey;
    private KeyContainer keyContainer = new();
    private string? keyFilePath;
    private string? keyFileHash;
    private string keyFileSnapshot = string.Empty;
    private string? loadedKeyReference;
    private string searchText = string.Empty;
    private string? keyFileLoadError;
    private bool keyFileLoadIsWarning;
    private readonly Dictionary<string, List<RadioAlias>> aliasTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> aliasFileSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> aliasLoadErrors = [];
    private readonly List<string> aliasLoadWarnings = [];
    private string loadedAliasReference = string.Empty;
    private ConfigurationAliasRow? selectedAlias;
    private readonly Dictionary<string, List<PatchMemberSetting>> stagedGroupMemberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> stagedGroupModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> lastSystemRenameTargets = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationStudioViewModel(
        ConfigurationDocument document,
        MainWindowViewModel runtimeViewModel,
        UserSettingsStore settingsStore,
        ConfigurationStudioSection initialSection)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.runtimeViewModel = runtimeViewModel ?? throw new ArgumentNullException(nameof(runtimeViewModel));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        Navigation =
        [
            new(ConfigurationStudioSection.Overview, "Overview", "File status and validation"),
            new(ConfigurationStudioSection.Systems, "FNE Systems", "Connections and credentials"),
            new(ConfigurationStudioSection.Zones, "Zones & Channels", "CPS-style channel editor"),
            new(ConfigurationStudioSection.Streams, "Web Streams", "Streams across all zones"),
            new(ConfigurationStudioSection.Groups, "Groups", "Definitions and operator state"),
            new(ConfigurationStudioSection.EncryptionKeys, "Encryption Keys", "Referenced local key file"),
            new(ConfigurationStudioSection.Files, "Files & Interoperability", "Paths, aliases, YAML, and exports")
        ];
        Replace(VisibleNavigation, Navigation);
        selectedNavigation = Navigation.First(item => item.Section == initialSection);
        originalSystems = document.Configuration.Systems.Select((system, index) => new SystemIdentity(
            system.Name,
            system.PeerId,
            system.Rid,
            system.Address,
            index)).ToArray();
        originalChannels = document.Configuration.Zones.SelectMany((zone, zoneIndex) =>
            zone.Channels.Select((channel, channelIndex) => new ChannelIdentity(
                channel.System,
                channel.Name,
                channel.Tgid,
                channel.Mode,
                zoneIndex,
                channelIndex))).ToArray();
        originalStreams = document.Configuration.Zones.SelectMany((zone, zoneIndex) =>
            zone.WebStreams.Select((stream, streamIndex) => new StreamIdentity(
                new WebStreamConfiguration
                {
                    Name = stream.Name,
                    Url = stream.Url,
                    AuthUsername = stream.AuthUsername,
                    AuthPassword = stream.AuthPassword,
                    IdleColor = stream.IdleColor
                },
                stream.Name,
                stream.Url,
                zoneIndex,
                streamIndex))).ToArray();
        foreach (SystemIdentity system in originalSystems)
            lastSystemRenameTargets[system.Name] = system.Name;
        originalGroupNames = document.Configuration.EffectiveGroups().Select(group => group.Name).ToArray();
        originalWidgetPositions = settingsStore.Load().ChannelWidgetPositions.ToDictionary(
            entry => entry.Key,
            entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y },
            StringComparer.OrdinalIgnoreCase);
        InitializeDraftWidgetPositions();
        lastSnapshot = Snapshot();
        LoadReferencedKeyFile();
        LoadReferencedAliasFiles();
        RefreshCollections();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ConfigurationStudioNavigationItem> Navigation { get; }
    public ObservableCollection<ConfigurationStudioNavigationItem> VisibleNavigation { get; } = [];
    public ObservableCollection<SystemConfiguration> Systems { get; } = [];
    public ObservableCollection<ZoneConfiguration> Zones { get; } = [];
    public ObservableCollection<ChannelConfiguration> Channels { get; } = [];
    public ObservableCollection<ConfigurationStreamRow> Streams { get; } = [];
    public ObservableCollection<GroupConfiguration> Groups { get; } = [];
    public ObservableCollection<KeyEntry> KeyEntries { get; } = [];
    public ObservableCollection<ConfigurationAliasRow> Aliases { get; } = [];
    public ObservableCollection<ConfigurationChannelPreviewViewModel> PreviewChannels { get; } = [];
    public ObservableCollection<ConfigurationValidationIssue> ValidationIssues { get; } = [];
    public IReadOnlyList<PatchGroupEditorViewModel> OperationalGroups => runtimeViewModel.PatchGroups;
    public IReadOnlyList<string> ModeOptions { get; } = ["p25", "dmr", "nxdn", "analog"];
    public IReadOnlyList<string> CardSizeOptions { get; } = ["small", "normal", "large"];
    public IReadOnlyList<string> TransportModeOptions { get; } = ["auto", "ecb", "cbc"];
    public IReadOnlyList<string> GroupTypeOptions { get; } = ["patch", "multiselect"];
    public IReadOnlyList<string> ProtocolOptions { get; } = ["p25", "dmr", "nxdn"];

    public ConfigurationDocument Document => document;
    public ConsoleConfiguration Configuration => document.Configuration;
    public bool CanEdit => !document.IsReadOnly;
    public bool CanExportSanitized => !document.IsReadOnly;
    public bool IsDirty => document.IsDirty || IsKeyFileDirty || AliasFilesDirty || LayoutChanged || stagedGroupMemberships.Count > 0;
    public bool IsKeyFileDirty => keyFilePath is not null &&
        !string.Equals(keyFileSnapshot, KeyFileLoader.Serialize(keyContainer), StringComparison.Ordinal);
    public bool LayoutChanged => draftWidgetPositions.Any(entry =>
        !originalWidgetPositions.TryGetValue(GetChannelSettingsKey(entry.Key), out WidgetPositionSetting? saved) ||
        Math.Abs(saved.X - entry.Value.X) >= 0.01 || Math.Abs(saved.Y - entry.Value.Y) >= 0.01);
    public bool AliasFilesDirty => aliasTables.Any(entry =>
        !aliasFileSnapshots.TryGetValue(entry.Key, out string? snapshot) ||
        !string.Equals(snapshot, AliasFileLoader.Serialize(entry.Value), StringComparison.Ordinal));
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool HasErrors => ValidationIssues.Any(issue => issue.IsError);
    public string DocumentPathText => document.SourcePath ?? "Unsaved configuration";
    public string StatusText => document.IsReadOnly
        ? document.ReadOnlyReason ?? "Read-only YAML"
        : IsDirty ? "Draft has unsaved changes" : "No unsaved changes";
    public string IssueSummary
    {
        get
        {
            int errors = ValidationIssues.Count(issue => issue.IsError);
            int warnings = ValidationIssues.Count - errors;
            return $"{errors} error{(errors == 1 ? string.Empty : "s")}, {warnings} warning{(warnings == 1 ? string.Empty : "s")}";
        }
    }
    public string ConfigurationShapeText
        => $"{Systems.Count} systems  •  {Zones.Count} zones  •  {Configuration.Zones.Sum(zone => zone.Channels.Count)} channels  •  {Streams.Count} streams  •  {Groups.Count} groups";
    public string UnknownFieldsText => document.UnknownFields.Count == 0
        ? "No unmatched YAML fields were found."
        : $"{document.UnknownFields.Count} unmatched field(s) will be preserved when their containing item is retained.";
    public string FullExportText => document.IsReadOnly ? document.SourceText : document.Serialize();
    public string KeyFilePathText => keyFilePath ?? "No key file is referenced.";
    public bool HasKeyFile => keyFilePath is not null;
    public bool CanUseOperationalGroups => document.SourcePath is not null &&
        runtimeViewModel.CurrentCodeplugPath is not null &&
        string.Equals(Path.GetFullPath(document.SourcePath), Path.GetFullPath(runtimeViewModel.CurrentCodeplugPath), StringComparison.OrdinalIgnoreCase);
    public string OperationalGroupHint => CanUseOperationalGroups
        ? "Enable and multi-select PTT actions affect the active console immediately. Apply operator state stages membership and direction for Review & Save."
        : "Operational controls are unavailable because this draft is unsaved or is not the active codeplug.";

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetField(ref searchText, value ?? string.Empty))
                return;
            string query = searchText.Trim();
            Replace(VisibleNavigation, query.Length == 0
                ? Navigation
                : Navigation.Where(item =>
                    item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    SectionContains(item.Section, query)));
        }
    }

    public ConfigurationStudioNavigationItem SelectedNavigation
    {
        get => selectedNavigation;
        set
        {
            if (value is null || ReferenceEquals(selectedNavigation, value))
                return;
            selectedNavigation = value;
            OnPropertyChanged();
            NotifySectionVisibility();
        }
    }

    public bool IsOverview => selectedNavigation.Section == ConfigurationStudioSection.Overview;
    public bool IsSystems => selectedNavigation.Section == ConfigurationStudioSection.Systems;
    public bool IsZones => selectedNavigation.Section == ConfigurationStudioSection.Zones;
    public bool IsStreams => selectedNavigation.Section == ConfigurationStudioSection.Streams;
    public bool IsGroups => selectedNavigation.Section == ConfigurationStudioSection.Groups;
    public bool IsEncryptionKeys => selectedNavigation.Section == ConfigurationStudioSection.EncryptionKeys;
    public bool IsFiles => selectedNavigation.Section == ConfigurationStudioSection.Files;

    public SystemConfiguration? SelectedSystem
    {
        get => selectedSystem;
        set => SetField(ref selectedSystem, value);
    }
    public ZoneConfiguration? SelectedZone
    {
        get => selectedZone;
        set
        {
            if (!SetField(ref selectedZone, value))
                return;
            RefreshChannelsAndPreview();
        }
    }
    public ChannelConfiguration? SelectedChannel
    {
        get => selectedChannel;
        set => SetField(ref selectedChannel, value);
    }
    public ConfigurationStreamRow? SelectedStream
    {
        get => selectedStream;
        set => SetField(ref selectedStream, value);
    }
    public GroupConfiguration? SelectedGroup
    {
        get => selectedGroup;
        set => SetField(ref selectedGroup, value);
    }
    public KeyEntry? SelectedKey
    {
        get => selectedKey;
        set => SetField(ref selectedKey, value);
    }
    public ConfigurationAliasRow? SelectedAlias
    {
        get => selectedAlias;
        set => SetField(ref selectedAlias, value);
    }

    public void SelectSection(ConfigurationStudioSection section)
        => SelectedNavigation = Navigation.First(item => item.Section == section);

    public void CommitFieldEdit()
    {
        if (!CanEdit)
            return;
        ApplySystemRenameReferences();
        string current = Snapshot();
        if (string.Equals(current, lastSnapshot, StringComparison.Ordinal))
            return;
        undo.Push(lastSnapshot);
        redo.Clear();
        lastSnapshot = current;
        document.MarkDirty();
        if (!string.Equals(loadedKeyReference, Configuration.KeyFile, StringComparison.Ordinal))
            LoadReferencedKeyFile();
        string aliasReference = CreateAliasReference();
        if (!string.Equals(loadedAliasReference, aliasReference, StringComparison.Ordinal))
            LoadReferencedAliasFiles();
        InitializeDraftWidgetPositions();
        RefreshCollections(preserveSelection: true);
    }

    public void CommitKeyEdit()
    {
        RefreshValidation();
        NotifyDocumentState();
    }

    public void CommitAliasEdit()
    {
        RefreshValidation();
        NotifyDocumentState();
    }

    public void Undo()
    {
        if (undo.Count == 0 || document.IsReadOnly)
            return;
        redo.Push(Snapshot());
        ReplaceDocument(undo.Pop());
    }

    public void Redo()
    {
        if (redo.Count == 0 || document.IsReadOnly)
            return;
        undo.Push(Snapshot());
        ReplaceDocument(redo.Pop());
    }

    public void AddSystem()
    {
        Mutate(() => Configuration.Systems.Add(new SystemConfiguration
        {
            Name = UniqueName("New System", Configuration.Systems.Select(system => system.Name)),
            Address = "127.0.0.1",
            Port = 62031,
            Identity = "DVM Console",
            TransportEncryptionMode = "auto"
        }));
        SelectedSystem = Configuration.Systems.Last();
    }

    public void DuplicateSystem()
    {
        if (SelectedSystem is null)
            return;
        SystemConfiguration source = SelectedSystem;
        Mutate(() => Configuration.Systems.Add(new SystemConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", Configuration.Systems.Select(system => system.Name)),
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
        }));
        SelectedSystem = Configuration.Systems.Last();
    }

    public void DeleteSystem()
    {
        if (SelectedSystem is { } system)
            Mutate(() => Configuration.Systems.Remove(system));
    }

    public void AddZone()
    {
        Mutate(() => Configuration.Zones.Add(new ZoneConfiguration
        {
            Name = UniqueName("New Zone", Configuration.Zones.Select(zone => zone.Name))
        }));
        SelectedZone = Configuration.Zones.Last();
    }

    public void DuplicateZone()
    {
        if (SelectedZone is null)
            return;
        ZoneConfiguration source = SelectedZone;
        Mutate(() => Configuration.Zones.Add(new ZoneConfiguration
        {
            Name = UniqueName($"{source.Name} Copy", Configuration.Zones.Select(zone => zone.Name)),
            TabColor = source.TabColor,
            TabTextColor = source.TabTextColor,
            Channels = source.Channels.Select(CloneChannel).ToList(),
            WebStreams = source.WebStreams.Select(CloneStream).ToList()
        }));
        SelectedZone = Configuration.Zones.Last();
    }

    public void DeleteZone()
    {
        if (SelectedZone is { } zone)
            Mutate(() => Configuration.Zones.Remove(zone));
    }

    public void AddChannel()
    {
        if (SelectedZone is null)
            return;
        string systemName = Configuration.Systems.FirstOrDefault()?.Name ?? string.Empty;
        Mutate(() => SelectedZone.Channels.Add(new ChannelConfiguration
        {
            Name = UniqueName("New Channel", Configuration.Zones.SelectMany(zone => zone.Channels).Select(channel => channel.Name)),
            System = systemName,
            Tgid = "1",
            CardSize = "normal"
        }));
        SelectedChannel = SelectedZone.Channels.Last();
    }

    public void DuplicateChannel()
    {
        if (SelectedZone is null || SelectedChannel is null)
            return;
        ChannelConfiguration copy = CloneChannel(SelectedChannel);
        copy.Name = UniqueName($"{SelectedChannel.Name} Copy", Configuration.Zones.SelectMany(zone => zone.Channels).Select(channel => channel.Name));
        Mutate(() => SelectedZone.Channels.Add(copy));
        SelectedChannel = copy;
    }

    public void DeleteChannel()
    {
        if (SelectedZone is { } zone && SelectedChannel is { } channel)
            Mutate(() => zone.Channels.Remove(channel));
    }

    public void MoveChannel(int offset)
    {
        if (SelectedZone is null || SelectedChannel is null)
            return;
        int index = SelectedZone.Channels.IndexOf(SelectedChannel);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= SelectedZone.Channels.Count)
            return;
        Mutate(() =>
        {
            SelectedZone.Channels.RemoveAt(index);
            SelectedZone.Channels.Insert(destination, SelectedChannel);
        });
    }

    public void SetChannelsRxOnly(IEnumerable<ChannelConfiguration> channels, bool rxOnly)
    {
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (selected.Length == 0)
            return;
        Mutate(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.RxOnly = rxOnly;
        });
    }

    public void ApplySelectedCardSize(IEnumerable<ChannelConfiguration> channels)
    {
        if (SelectedChannel is not { } source)
            return;
        ChannelConfiguration[] selected = channels.Distinct().ToArray();
        if (selected.Length == 0)
            return;
        Mutate(() =>
        {
            foreach (ChannelConfiguration channel in selected)
                channel.CardSize = source.CardSize;
        });
    }

    public void AddStream()
    {
        ZoneConfiguration? zone = SelectedZone ?? Configuration.Zones.FirstOrDefault();
        if (zone is null)
            return;
        var stream = new WebStreamConfiguration
        {
            Name = UniqueName("New Stream", Configuration.Zones.SelectMany(item => item.WebStreams).Select(item => item.Name)),
            Url = "https://example.invalid/stream"
        };
        Mutate(() => zone.WebStreams.Add(stream));
        SelectedStream = Streams.FirstOrDefault(row => ReferenceEquals(row.Stream, stream));
    }

    public void DeleteStream()
    {
        if (SelectedStream is { } row)
            Mutate(() => row.Zone.WebStreams.Remove(row.Stream));
    }

    public void MoveSelectedStreamTo(ZoneConfiguration zone)
    {
        if (SelectedStream is not { } row || ReferenceEquals(row.Zone, zone))
            return;
        WebStreamConfiguration stream = row.Stream;
        Mutate(() =>
        {
            row.Zone.WebStreams.Remove(stream);
            zone.WebStreams.Add(stream);
        });
        SelectedStream = Streams.FirstOrDefault(item => ReferenceEquals(item.Stream, stream));
    }

    public void AddGroup()
    {
        Mutate(() => Configuration.Groups.Add(new GroupConfiguration
        {
            Name = UniqueName("New Group", Configuration.Groups.Select(group => group.Name)),
            Type = "patch"
        }));
        SelectedGroup = Configuration.Groups.Last();
    }

    public void DeleteGroup()
    {
        if (SelectedGroup is { } group)
            Mutate(() => Configuration.Groups.Remove(group));
    }

    public void AddKey()
    {
        if (!HasKeyFile)
            return;
        var key = new KeyEntry { Protocol = "p25", KeyId = 1, AlgId = 0, Key = string.Empty };
        keyContainer.Keys.Add(key);
        KeyEntries.Add(key);
        SelectedKey = key;
        keyFileLoadError = null;
        keyFileLoadIsWarning = false;
        CommitKeyEdit();
    }

    public void DeleteKey()
    {
        if (SelectedKey is not { } key)
            return;
        keyContainer.Keys.Remove(key);
        KeyEntries.Remove(key);
        SelectedKey = KeyEntries.FirstOrDefault();
        CommitKeyEdit();
    }

    public void AddAlias()
    {
        KeyValuePair<string, List<RadioAlias>> table = aliasTables.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(table.Key))
            return;
        uint nextRid = table.Value.Count == 0 ? 1 : table.Value.Max(alias => alias.Rid) + 1;
        var alias = new RadioAlias { Rid = nextRid, Alias = "New Alias" };
        table.Value.Add(alias);
        var row = new ConfigurationAliasRow(table.Key, alias);
        Aliases.Add(row);
        SelectedAlias = row;
        CommitAliasEdit();
    }

    public void DeleteAlias()
    {
        if (SelectedAlias is not { } row || !aliasTables.TryGetValue(row.FilePath, out List<RadioAlias>? table))
            return;
        table.Remove(row.Alias);
        Aliases.Remove(row);
        SelectedAlias = Aliases.FirstOrDefault();
        CommitAliasEdit();
    }

    public string? StageOperationalGroup(PatchGroupEditorViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.GetMembershipValidationError() is { } error)
            return error;
        stagedGroupMemberships[group.Name] = group.GetMembersInRoutingOrder()
            .Select(member => new PatchMemberSetting
            {
                SystemName = member.Channel.Definition.SystemName,
                DestinationId = member.Channel.Definition.DestinationId,
                ChannelName = member.Channel.Name
            })
            .ToList();
        stagedGroupModes[group.Name] = group.IsPatchGroup && group.IsOneWay;
        NotifyDocumentState();
        return null;
    }

    public ConfigurationSavePlan CreateSavePlan(string destinationPath)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        var issues = ValidationIssues.ToList();
        var files = new List<ConfigurationFileChange>
        {
            new(
                fullDestination,
                document.Serialize(),
                document.SourcePath is not null && string.Equals(fullDestination, document.SourcePath, StringComparison.OrdinalIgnoreCase)
                    ? document.SourceHash
                    : null,
                "Codeplug",
                ContainsSecrets: true)
        };

        if (keyFilePath is not null)
        {
            string keyTarget = ResolveReferencedSaveTarget(
                Configuration.KeyFile,
                keyFilePath,
                fullDestination);
            if (IsKeyFileDirty || !string.Equals(keyTarget, keyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new ConfigurationFileChange(
                    keyTarget,
                    KeyFileLoader.Serialize(keyContainer),
                    GetExpectedHash(keyTarget, keyFilePath, keyFileHash),
                    "Encryption key file",
                    ContainsSecrets: true));
            }
        }

        foreach (KeyValuePair<string, List<RadioAlias>> aliasTable in aliasTables)
        {
            string serialized = AliasFileLoader.Serialize(aliasTable.Value);
            bool dirty = !aliasFileSnapshots.TryGetValue(aliasTable.Key, out string? snapshot) ||
                !string.Equals(snapshot, serialized, StringComparison.Ordinal);
            foreach (string target in GetAliasSaveTargets(aliasTable.Key, fullDestination))
            {
                if (!dirty && string.Equals(target, aliasTable.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add(new ConfigurationFileChange(
                    target,
                    serialized,
                    GetExpectedHash(target, aliasTable.Key, aliasFileHashes.GetValueOrDefault(aliasTable.Key)),
                    "RID alias file",
                    ContainsSecrets: false));
            }
        }

        UserSettings settings = settingsStore.Load();
        if (document.SourcePath is not null && !string.Equals(document.SourcePath, fullDestination, StringComparison.OrdinalIgnoreCase))
            CodeplugGroupStateStore.CopyForSaveAs(settings, document.SourcePath, fullDestination);
        ApplyIdentityMigrations(settings, fullDestination, fullDestination);
        CodeplugGroupState destinationGroupState = CodeplugGroupStateStore.GetOrMigrate(settings, fullDestination);
        Dictionary<string, string> groupRenames = BuildGroupMigrations().Renames;
        foreach (KeyValuePair<string, List<PatchMemberSetting>> membership in stagedGroupMemberships)
            destinationGroupState.Memberships[groupRenames.GetValueOrDefault(membership.Key, membership.Key)] = membership.Value;
        foreach (KeyValuePair<string, bool> mode in stagedGroupModes)
            destinationGroupState.OneWayModes[groupRenames.GetValueOrDefault(mode.Key, mode.Key)] = mode.Value;
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> position in draftWidgetPositions)
        {
            settings.ChannelWidgetPositions[GetChannelSettingsKey(position.Key)] = new WidgetPositionSetting
            {
                X = position.Value.X,
                Y = position.Value.Y
            };
        }
        UserSettingsSnapshot settingsSnapshot = settingsStore.CaptureSnapshot(settings);
        files.Add(new ConfigurationFileChange(
            settingsStore.Path,
            settingsSnapshot.Json,
            File.Exists(settingsStore.Path) ? ConfigurationDocument.ComputeFileHash(settingsStore.Path) : null,
            "Operator settings",
            ContainsSecrets: false));

        return new ConfigurationSavePlan(files, issues);
    }

    private string ResolveReferencedSaveTarget(
        string? reference,
        string currentResolvedPath,
        string codeplugDestination)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || document.SourcePath is null)
            return currentResolvedPath;
        string sourceDirectory = Path.GetDirectoryName(document.SourcePath) ?? AppContext.BaseDirectory;
        string destinationDirectory = Path.GetDirectoryName(codeplugDestination) ?? AppContext.BaseDirectory;
        if (string.Equals(sourceDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            return currentResolvedPath;
        return Path.GetFullPath(Path.Combine(destinationDirectory, reference));
    }

    private IReadOnlyList<string> GetAliasSaveTargets(string currentResolvedPath, string codeplugDestination)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string sourceResolved;
            try
            {
                sourceResolved = ConfigurationLoader.ResolvePath(Configuration, system.AliasPath);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (string.Equals(sourceResolved, currentResolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(ResolveReferencedSaveTarget(
                    system.AliasPath,
                    currentResolvedPath,
                    codeplugDestination));
            }
        }
        if (targets.Count == 0)
            targets.Add(currentResolvedPath);
        return targets.ToArray();
    }

    private static string? GetExpectedHash(string target, string source, string? sourceHash)
    {
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            return sourceHash;
        return File.Exists(target) ? ConfigurationDocument.ComputeFileHash(target) : null;
    }

    public void AcceptSaved(string path, ConfigurationSavePlan plan)
    {
        string codeplugText = plan.Files.First(file => file.Category == "Codeplug").Content;
        document.AcceptSaved(path, codeplugText);
        LoadReferencedKeyFile();
        LoadReferencedAliasFiles();
        originalWidgetPositions.Clear();
        foreach (KeyValuePair<ChannelConfiguration, WidgetPositionSetting> position in draftWidgetPositions)
        {
            originalWidgetPositions[GetChannelSettingsKey(position.Key)] = new WidgetPositionSetting
            {
                X = position.Value.X,
                Y = position.Value.Y
            };
        }
        undo.Clear();
        redo.Clear();
        stagedGroupMemberships.Clear();
        stagedGroupModes.Clear();
        lastSnapshot = Snapshot();
        NotifyDocumentState();
    }

    public string BuildReviewText(ConfigurationSavePlan plan)
    {
        string changes = string.Join("\n", plan.Files.Select(file =>
            $"• {file.Category}: {file.Path}"));
        string compatibility = document.UnknownFields.Count > 0
            ? $"\n\n{document.UnknownFields.Count} unmatched YAML field(s) will be retained."
            : string.Empty;
        string migrations = BuildIdentityMigrationSummary();
        return $"Configuration Studio will write:\n\n{changes}{compatibility}{migrations}\n\n" +
               "Edited sections are written in canonical YAML. Comments and hand formatting inside those sections may change. " +
               "Restricted backups are created before any original file is replaced.";
    }

    public void MovePreviewChannel(ConfigurationChannelPreviewViewModel preview, double x, double y)
    {
        preview.X = Math.Round(Math.Max(0, x) / 10) * 10;
        preview.Y = Math.Round(Math.Max(0, y) / 10) * 10;
        draftWidgetPositions[preview.Channel] = new WidgetPositionSetting { X = preview.X, Y = preview.Y };
        NotifyDocumentState();
    }

    private void ApplyIdentityMigrations(UserSettings settings, string statePath, string destinationPath)
    {
        Dictionary<string, string> systemRenames = BuildSystemRenames();
        HashSet<string> currentSystemNames = Configuration.Systems.Select(system => system.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] deletedSystems = originalSystems
            .Where(system => !currentSystemNames.Contains(system.Name) && !systemRenames.ContainsKey(system.Name))
            .Select(system => system.Name)
            .ToArray();

        foreach (KeyValuePair<string, string> rename in systemRenames)
        {
            MoveDictionaryEntry(settings.RxJitterBuffersBySystem, rename.Key, rename.Value);
            if (string.Equals(settings.LastSelectedSystemName, rename.Key, StringComparison.OrdinalIgnoreCase))
                settings.LastSelectedSystemName = rename.Value;
        }
        foreach (string deleted in deletedSystems)
        {
            settings.RxJitterBuffersBySystem.Remove(deleted);
            if (string.Equals(settings.LastSelectedSystemName, deleted, StringComparison.OrdinalIgnoreCase))
                settings.LastSelectedSystemName = null;
        }

        CodeplugGroupState state = CodeplugGroupStateStore.GetOrMigrate(settings, statePath);
        foreach (List<PatchMemberSetting> members in state.Memberships.Values)
        {
            members.RemoveAll(member => deletedSystems.Contains(member.SystemName, StringComparer.OrdinalIgnoreCase));
            foreach (PatchMemberSetting member in members)
            {
                if (systemRenames.TryGetValue(member.SystemName, out string? renamed))
                    member.SystemName = renamed;
            }
        }

        (Dictionary<string, string> groupRenames, IReadOnlyList<string> deletedGroups) = BuildGroupMigrations();
        foreach (KeyValuePair<string, string> rename in groupRenames)
        {
            MoveDictionaryEntry(state.Memberships, rename.Key, rename.Value);
            MoveDictionaryEntry(state.OneWayModes, rename.Key, rename.Value);
            MoveDictionaryEntry(state.EnabledStates, rename.Key, rename.Value);
        }

        foreach (string deleted in deletedGroups)
        {
            state.Memberships.Remove(deleted);
            state.OneWayModes.Remove(deleted);
            state.EnabledStates.Remove(deleted);
        }

        foreach ((string original, string? current) in BuildChannelMigrations(systemRenames))
        {
            if (current is not null)
            {
                MoveDictionaryEntry(settings.ChannelWidgetPositions, original, current);
                MoveDictionaryEntry(settings.ChannelVolumes, original, current);
                MoveDictionaryEntry(settings.ChannelStereoBalances, original, current);
                MoveDictionaryEntry(settings.ChannelOutputDeviceIds, original, current);
                MoveDictionaryEntry(settings.RecordingIgnoredSubscriberIds, original, current);
                MoveDictionaryEntry(settings.TransmitEncryptionStates, original, current);
                MoveListEntry(settings.ReceiveEnabledChannelKeys, original, current);
                MoveListEntry(settings.TransmitSelectedChannelKeys, original, current);
                MoveListEntry(settings.RecordingEnabledChannelKeys, original, current);
                if (string.Equals(settings.LastSelectedChannelKey, original, StringComparison.OrdinalIgnoreCase))
                    settings.LastSelectedChannelKey = current;
            }
            else
            {
                settings.ChannelWidgetPositions.Remove(original);
                settings.ChannelVolumes.Remove(original);
                settings.ChannelStereoBalances.Remove(original);
                settings.ChannelOutputDeviceIds.Remove(original);
                settings.RecordingIgnoredSubscriberIds.Remove(original);
                settings.TransmitEncryptionStates.Remove(original);
                RemoveListEntry(settings.ReceiveEnabledChannelKeys, original);
                RemoveListEntry(settings.TransmitSelectedChannelKeys, original);
                RemoveListEntry(settings.RecordingEnabledChannelKeys, original);
                if (string.Equals(settings.LastSelectedChannelKey, original, StringComparison.OrdinalIgnoreCase))
                    settings.LastSelectedChannelKey = null;
            }
        }

        foreach (List<PatchMemberSetting> members in state.Memberships.Values)
            foreach (PatchMemberSetting member in members)
            {
                string originalMemberSystem = systemRenames.FirstOrDefault(rename =>
                    string.Equals(rename.Value, member.SystemName, StringComparison.OrdinalIgnoreCase)).Key ?? member.SystemName;
                ChannelIdentity? originalChannel = originalChannels.FirstOrDefault(channel =>
                    string.Equals(channel.System, originalMemberSystem, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(channel.Name, member.ChannelName, StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(channel.DestinationId, out uint destinationId) && destinationId == member.DestinationId);
                if (originalChannel is null)
                    continue;
                string migratedSystem = systemRenames.GetValueOrDefault(originalChannel.System, originalChannel.System);
                ChannelConfiguration? currentChannel = FindCurrentChannel(originalChannel, migratedSystem);
                if (currentChannel is not null)
                    member.ChannelName = currentChannel.Name;
            }

        bool pathChanged = document.SourcePath is not null &&
            !string.Equals(document.SourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
        foreach ((StreamIdentity original, WebStreamConfiguration? current) in BuildStreamMigrations(pathChanged))
        {
            if (current is null)
            {
                settings.WebStreamOutputDeviceIds.Remove(original.Name);
                settings.WebStreamVolumes.Remove(original.Name);
                continue;
            }
            if (!string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase))
            {
                MoveDictionaryEntry(settings.WebStreamOutputDeviceIds, original.Name, current.Name);
                MoveDictionaryEntry(settings.WebStreamVolumes, original.Name, current.Name);
            }

            string oldIdentity = WebStreamSelectionIdentity.Create(document.SourcePath ?? statePath, original.Configuration);
            string newIdentity = WebStreamSelectionIdentity.Create(destinationPath, current);
            if (oldIdentity.Length > 0 && newIdentity.Length > 0)
                MoveListEntry(settings.SelectedWebStreams, oldIdentity, newIdentity, StringComparison.Ordinal);
        }
    }

    private Dictionary<string, string> BuildSystemRenames()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentNames = Configuration.Systems.Select(system => system.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> originalNames = originalSystems.Select(system => system.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SystemIdentity original in originalSystems.Where(system => !currentNames.Contains(system.Name)))
        {
            List<SystemConfiguration> candidates = Configuration.Systems.Where(system =>
                !originalNames.Contains(system.Name) &&
                ((original.PeerId != 0 && system.PeerId == original.PeerId) ||
                 (!string.IsNullOrWhiteSpace(original.Rid) && string.Equals(system.Rid, original.Rid, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(original.Address) && string.Equals(system.Address, original.Address, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            SystemConfiguration? current = candidates.Count == 1
                ? candidates[0]
                : original.Index < Configuration.Systems.Count && !originalNames.Contains(Configuration.Systems[original.Index].Name)
                    ? Configuration.Systems[original.Index]
                    : null;
            if (current is not null)
                renames[original.Name] = current.Name;
        }
        return renames;
    }

    private void ApplySystemRenameReferences()
    {
        Dictionary<string, string> renames = BuildSystemRenames();
        foreach (SystemIdentity original in originalSystems)
        {
            string previous = lastSystemRenameTargets.GetValueOrDefault(original.Name, original.Name);
            string current = renames.GetValueOrDefault(original.Name, original.Name);
            if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (ChannelConfiguration channel in Configuration.Zones.SelectMany(zone => zone.Channels))
            {
                if (string.Equals(channel.System, previous, StringComparison.OrdinalIgnoreCase))
                    channel.System = current;
            }
            lastSystemRenameTargets[original.Name] = current;
        }
    }

    private (Dictionary<string, string> Renames, IReadOnlyList<string> Deleted) BuildGroupMigrations()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> currentNames = Configuration.Groups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> originalNames = originalGroupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatchedCurrent = Configuration.Groups.Select(group => group.Name).Where(name => !originalNames.Contains(name)).ToList();
        var unmatchedOriginal = originalGroupNames.Where(name => !currentNames.Contains(name)).ToList();
        int renameCount = Math.Min(unmatchedOriginal.Count, unmatchedCurrent.Count);
        for (int index = 0; index < renameCount; index++)
            renames[unmatchedOriginal[index]] = unmatchedCurrent[index];
        return (renames, unmatchedOriginal.Skip(renameCount).ToArray());
    }

    private IReadOnlyList<(string Original, string? Current)> BuildChannelMigrations(
        IReadOnlyDictionary<string, string> systemRenames)
    {
        var migrations = new List<(string Original, string? Current)>();
        foreach (ChannelIdentity original in originalChannels)
        {
            string migratedSystem = systemRenames.GetValueOrDefault(original.System, original.System);
            ChannelConfiguration? current = FindCurrentChannel(original, migratedSystem);
            string originalKey = $"{original.System}\u001F{original.Name}";
            string? currentKey = current is null ? null : $"{current.System}\u001F{current.Name}";
            if (!string.Equals(originalKey, currentKey, StringComparison.OrdinalIgnoreCase))
                migrations.Add((originalKey, currentKey));
        }
        return migrations;
    }

    private ChannelConfiguration? FindCurrentChannel(ChannelIdentity original, string migratedSystem)
    {
        List<ChannelConfiguration> channels = Configuration.Zones.SelectMany(zone => zone.Channels).ToList();
        ChannelConfiguration? exact = channels.FirstOrDefault(channel =>
            string.Equals(channel.System, migratedSystem, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(channel.Name, original.Name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        List<ChannelConfiguration> stableMatches = channels.Where(channel =>
            string.Equals(channel.System, migratedSystem, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(channel.Tgid, original.DestinationId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(channel.Mode, original.Mode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stableMatches.Count == 1)
            return stableMatches[0];
        return originalChannels.Length == channels.Count &&
               original.ZoneIndex < Configuration.Zones.Count &&
               original.ChannelIndex < Configuration.Zones[original.ZoneIndex].Channels.Count
            ? Configuration.Zones[original.ZoneIndex].Channels[original.ChannelIndex]
            : null;
    }

    private IReadOnlyList<(StreamIdentity Original, WebStreamConfiguration? Current)> BuildStreamMigrations(
        bool includeUnchanged = false)
    {
        var migrations = new List<(StreamIdentity, WebStreamConfiguration?)>();
        List<WebStreamConfiguration> currentStreams = Configuration.Zones.SelectMany(zone => zone.WebStreams).ToList();
        foreach (StreamIdentity original in originalStreams)
        {
            WebStreamConfiguration? current = currentStreams.FirstOrDefault(stream =>
                string.Equals(stream.Name, original.Name, StringComparison.OrdinalIgnoreCase));
            current ??= currentStreams.Count(stream => string.Equals(stream.Url, original.Url, StringComparison.OrdinalIgnoreCase)) == 1
                ? currentStreams.First(stream => string.Equals(stream.Url, original.Url, StringComparison.OrdinalIgnoreCase))
                : null;
            current ??= originalStreams.Length == currentStreams.Count &&
                        original.ZoneIndex < Configuration.Zones.Count &&
                        original.StreamIndex < Configuration.Zones[original.ZoneIndex].WebStreams.Count
                ? Configuration.Zones[original.ZoneIndex].WebStreams[original.StreamIndex]
                : null;
            if (includeUnchanged || current is null || !string.Equals(original.Name, current.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(original.Url, current.Url, StringComparison.OrdinalIgnoreCase))
            {
                migrations.Add((original, current));
            }
        }
        return migrations;
    }

    private string BuildIdentityMigrationSummary()
    {
        Dictionary<string, string> systems = BuildSystemRenames();
        HashSet<string> currentSystemNames = Configuration.Systems.Select(system => system.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] deletedSystems = originalSystems
            .Where(system => !currentSystemNames.Contains(system.Name) && !systems.ContainsKey(system.Name))
            .Select(system => system.Name)
            .ToArray();
        (Dictionary<string, string> groups, IReadOnlyList<string> deletedGroups) = BuildGroupMigrations();
        IReadOnlyList<(string Original, string? Current)> channels = BuildChannelMigrations(systems);
        IReadOnlyList<(StreamIdentity Original, WebStreamConfiguration? Current)> streams = BuildStreamMigrations();
        var lines = new List<string>();
        lines.AddRange(systems.Select(rename => $"• System state: {rename.Key} → {rename.Value}"));
        lines.AddRange(deletedSystems.Select(name => $"• Remove system state: {name}"));
        lines.AddRange(groups.Select(rename => $"• Group state: {rename.Key} → {rename.Value}"));
        lines.AddRange(deletedGroups.Select(name => $"• Remove group state: {name}"));
        lines.AddRange(channels.Select(change => change.Current is null
            ? $"• Remove channel state: {change.Original.Replace('\u001F', '/')}"
            : $"• Channel state: {change.Original.Replace('\u001F', '/')} → {change.Current.Replace('\u001F', '/')}"));
        lines.AddRange(streams.Select(change => change.Current is null
            ? $"• Remove stream state: {change.Original.Name}"
            : $"• Stream state: {change.Original.Name} → {change.Current.Name}"));
        return lines.Count == 0 ? string.Empty : "\n\nOperator-state migrations:\n" + string.Join("\n", lines);
    }

    private static void MoveDictionaryEntry<T>(Dictionary<string, T> dictionary, string original, string current)
    {
        if (dictionary.Remove(original, out T? value))
            dictionary[current] = value;
    }

    private static void MoveListEntry(
        List<string> list,
        string original,
        string current,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        int index = list.FindIndex(value => string.Equals(value, original, comparison));
        if (index >= 0)
            list[index] = current;
    }

    private static void RemoveListEntry(List<string> list, string value)
        => list.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private void Mutate(Action action)
    {
        if (!CanEdit)
            return;
        undo.Push(Snapshot());
        redo.Clear();
        action();
        document.MarkDirty();
        lastSnapshot = Snapshot();
        RefreshCollections(preserveSelection: true);
    }

    private void ReplaceDocument(string yaml)
    {
        string? sourcePath = document.SourcePath;
        document = ConfigurationDocument.Parse(yaml, sourcePath);
        document.MarkDirty();
        lastSystemRenameTargets.Clear();
        Dictionary<string, string> systemRenames = BuildSystemRenames();
        foreach (SystemIdentity system in originalSystems)
            lastSystemRenameTargets[system.Name] = systemRenames.GetValueOrDefault(system.Name, system.Name);
        lastSnapshot = yaml;
        draftWidgetPositions.Clear();
        InitializeDraftWidgetPositions();
        LoadReferencedKeyFile();
        RefreshCollections();
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(Configuration));
    }

    private void RefreshCollections(bool preserveSelection = false)
    {
        string? systemName = preserveSelection ? SelectedSystem?.Name : null;
        string? zoneName = preserveSelection ? SelectedZone?.Name : null;
        string? channelName = preserveSelection ? SelectedChannel?.Name : null;
        string? streamName = preserveSelection ? SelectedStream?.Stream.Name : null;
        string? groupName = preserveSelection ? SelectedGroup?.Name : null;

        Replace(Systems, Configuration.Systems);
        Replace(Zones, Configuration.Zones);
        Replace(Streams, Configuration.Zones.SelectMany(zone => zone.WebStreams.Select(stream => new ConfigurationStreamRow(zone, stream))));
        Replace(Groups, Configuration.Groups);
        SelectedSystem = Systems.FirstOrDefault(system => string.Equals(system.Name, systemName, StringComparison.OrdinalIgnoreCase)) ?? Systems.FirstOrDefault();
        SelectedZone = Zones.FirstOrDefault(zone => string.Equals(zone.Name, zoneName, StringComparison.OrdinalIgnoreCase)) ?? Zones.FirstOrDefault();
        SelectedChannel = Channels.FirstOrDefault(channel => string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase)) ?? Channels.FirstOrDefault();
        SelectedStream = Streams.FirstOrDefault(row => string.Equals(row.Stream.Name, streamName, StringComparison.OrdinalIgnoreCase)) ?? Streams.FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)) ?? Groups.FirstOrDefault();
        RefreshValidation();
        NotifyDocumentState();
    }

    private void RefreshChannelsAndPreview()
    {
        Replace(Channels, SelectedZone?.Channels ?? []);
        PreviewChannels.Clear();
        if (SelectedZone is null)
            return;
        for (int index = 0; index < SelectedZone.Channels.Count; index++)
        {
            ChannelConfiguration channel = SelectedZone.Channels[index];
            string key = GetChannelSettingsKey(channel);
            WidgetPositionSetting position = draftWidgetPositions[channel];
            double x = position.X;
            double y = position.Y;
            PreviewChannels.Add(new ConfigurationChannelPreviewViewModel(
                channel,
                x,
                y,
                runtimeViewModel.ChannelCardHeight));
        }
        SelectedChannel = Channels.FirstOrDefault();
        OnPropertyChanged(nameof(LayoutChanged));
    }

    private void RefreshValidation()
    {
        Replace(ValidationIssues, document.Validate().Concat(ValidateKeys()));
        if (keyFileLoadError is not null)
        {
            ValidationIssues.Add(new ConfigurationValidationIssue(
                keyFileLoadIsWarning ? ConfigurationValidationSeverity.Warning : ConfigurationValidationSeverity.Error,
                "Encryption Keys",
                "keyFile",
                keyFileLoadError));
        }
        foreach (string error in aliasLoadErrors)
            ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Error, "Files & Interoperability", "aliasPath", error));
        foreach (string warning in aliasLoadWarnings)
            ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Warning, "Files & Interoperability", "aliasPath", warning));
        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
            foreach (IGrouping<uint, RadioAlias> duplicate in table.Value.GroupBy(alias => alias.Rid).Where(group => group.Count() > 1))
                ValidationIssues.Add(new ConfigurationValidationIssue(ConfigurationValidationSeverity.Error, "Files & Interoperability", table.Key, $"RID {duplicate.Key} is duplicated in '{table.Key}'."));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(IssueSummary));
    }

    private IEnumerable<ConfigurationValidationIssue> ValidateKeys()
        => KeyFileValidator.Validate(keyContainer);

    private void LoadReferencedKeyFile()
    {
        keyContainer = new KeyContainer();
        keyFilePath = null;
        keyFileHash = null;
        keyFileSnapshot = string.Empty;
        keyFileLoadError = null;
        keyFileLoadIsWarning = false;
        loadedKeyReference = Configuration.KeyFile;
        if (string.IsNullOrWhiteSpace(Configuration.KeyFile) || document.SourcePath is null)
        {
            Replace(KeyEntries, Array.Empty<KeyEntry>());
            return;
        }
        try
        {
            keyFilePath = ConfigurationLoader.ResolvePath(Configuration, Configuration.KeyFile);
            if (File.Exists(keyFilePath))
            {
                keyContainer = KeyFileLoader.Load(keyFilePath);
                keyFileHash = ConfigurationDocument.ComputeFileHash(keyFilePath);
                keyFileSnapshot = KeyFileLoader.Serialize(keyContainer);
            }
            else
            {
                keyFileLoadError = $"The referenced key file does not exist: {keyFilePath}";
                keyFileLoadIsWarning = true;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            keyFileLoadError = $"The referenced key file could not be opened: {exception.Message}";
        }
        Replace(KeyEntries, keyContainer.Keys);
        SelectedKey = KeyEntries.FirstOrDefault();
        OnPropertyChanged(nameof(KeyFilePathText));
        OnPropertyChanged(nameof(HasKeyFile));
    }

    private void LoadReferencedAliasFiles()
    {
        aliasTables.Clear();
        aliasFileHashes.Clear();
        aliasFileSnapshots.Clear();
        aliasLoadErrors.Clear();
        aliasLoadWarnings.Clear();
        Aliases.Clear();
        loadedAliasReference = CreateAliasReference();
        if (document.SourcePath is null)
            return;

        foreach (SystemConfiguration system in Configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.AliasPath))
                continue;
            string path;
            try
            {
                path = ConfigurationLoader.ResolvePath(Configuration, system.AliasPath);
                if (!aliasTables.TryGetValue(path, out List<RadioAlias>? aliases))
                {
                    if (!File.Exists(path))
                    {
                        aliases = [];
                        aliasTables[path] = aliases;
                        aliasFileSnapshots[path] = string.Empty;
                        aliasLoadWarnings.Add($"Alias file for system '{system.Name}' does not exist yet: {path}");
                        continue;
                    }
                    aliases = AliasFileLoader.Load(path);
                    aliasTables[path] = aliases;
                    aliasFileHashes[path] = ConfigurationDocument.ComputeFileHash(path);
                    aliasFileSnapshots[path] = AliasFileLoader.Serialize(aliases);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
            {
                aliasLoadErrors.Add($"Alias file for system '{system.Name}' could not be opened: {exception.Message}");
            }
        }

        foreach (KeyValuePair<string, List<RadioAlias>> table in aliasTables)
            foreach (RadioAlias alias in table.Value)
                Aliases.Add(new ConfigurationAliasRow(table.Key, alias));
        SelectedAlias = Aliases.FirstOrDefault();
        OnPropertyChanged(nameof(AliasFilesDirty));
    }

    private string CreateAliasReference()
        => string.Join("\u001F", Configuration.Systems.Select(system => system.AliasPath ?? string.Empty));

    private string Snapshot() => document.IsReadOnly ? document.SourceText : document.Serialize();

    private void InitializeDraftWidgetPositions()
    {
        foreach (ZoneConfiguration zone in Configuration.Zones)
        {
            double x = 0;
            double y = 0;
            foreach (ChannelConfiguration channel in zone.Channels)
            {
                if (!draftWidgetPositions.ContainsKey(channel))
                {
                    string key = GetChannelSettingsKey(channel);
                    draftWidgetPositions[channel] = originalWidgetPositions.TryGetValue(key, out WidgetPositionSetting? saved)
                        ? new WidgetPositionSetting { X = saved.X, Y = saved.Y }
                        : new WidgetPositionSetting { X = x, Y = y };
                }

                double width = ChannelViewModel.ResolveCardWidth(channel.CardSize);
                x += width + MainWindowViewModel.ChannelWidgetSpacing;
                if (x + 180 > 980)
                {
                    x = 0;
                    y += runtimeViewModel.ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing;
                }
            }
        }
    }

    private bool SectionContains(ConfigurationStudioSection section, string query)
        => section switch
        {
            ConfigurationStudioSection.Systems => Configuration.Systems.Any(system =>
                system.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                system.Address.Contains(query, StringComparison.OrdinalIgnoreCase)),
            ConfigurationStudioSection.Zones => Configuration.Zones.Any(zone =>
                zone.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                zone.Channels.Any(channel => channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase))),
            ConfigurationStudioSection.Streams => Configuration.Zones.SelectMany(zone => zone.WebStreams).Any(stream =>
                stream.Name.Contains(query, StringComparison.OrdinalIgnoreCase)),
            ConfigurationStudioSection.Groups => Configuration.Groups.Any(group =>
                group.Name.Contains(query, StringComparison.OrdinalIgnoreCase)),
            ConfigurationStudioSection.EncryptionKeys => keyContainer.Keys.Any(key =>
                key.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                key.KeyId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)),
            ConfigurationStudioSection.Files => Configuration.Systems.Any(system =>
                system.AliasPath.Contains(query, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };

    private void NotifySectionVisibility()
    {
        foreach (string name in new[]
                 {
                     nameof(IsOverview), nameof(IsSystems), nameof(IsZones), nameof(IsStreams),
                     nameof(IsGroups), nameof(IsEncryptionKeys), nameof(IsFiles)
                 })
            OnPropertyChanged(name);
    }

    private void NotifyDocumentState()
    {
        foreach (string name in new[]
                 {
                     nameof(IsDirty), nameof(StatusText), nameof(CanUndo), nameof(CanRedo),
                     nameof(ConfigurationShapeText), nameof(UnknownFieldsText), nameof(LayoutChanged)
                 })
            OnPropertyChanged(name);
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

    private static string GetChannelSettingsKey(ChannelConfiguration channel)
        => $"{channel.System}\u001F{channel.Name}";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (T item in source)
            target.Add(item);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
