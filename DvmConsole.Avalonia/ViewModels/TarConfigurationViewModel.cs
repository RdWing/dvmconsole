// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Headless TAR configuration surface for the Avalonia shell: a WPF-compatible
    /// projection of codeplug zones/channels into editable per-resource items,
    /// ignored-RID parsing, recording-root validation, and save payload emission.
    /// The WPF <c>TarConfigurationWindow</c> logic is ported with no Avalonia
    /// controls, dispatcher, timers, file dialogs, SettingsManager, or
    /// WPF/NAudio references — persistence and the XAML surface are later seams
    /// wired through <see cref="SaveRequested"/>.
    /// </summary>
    public sealed class TarConfigurationViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// One editable channel/resource row. Read-only identity columns are
        /// fixed at construction; the three mutable settings raise change-only
        /// <see cref="PropertyChanged"/> and are mirrored across every item
        /// sharing the same resource key (case-insensitive) by the parent.
        /// </summary>
        public sealed class TarChannelConfigItem : INotifyPropertyChanged
        {
            private bool enabled;
            private int retentionDays;
            private string ignoredSubscriberIdsText;

            /// <summary>
            /// Creates an item with fixed identity columns and default mutable state.
            /// </summary>
            public TarChannelConfigItem(
                string systemName,
                string channelName,
                string talkgroupId,
                string resourceKey,
                string mode)
            {
                SystemName = systemName ?? string.Empty;
                ChannelName = channelName ?? string.Empty;
                TalkgroupId = talkgroupId ?? string.Empty;
                ResourceKey = resourceKey ?? string.Empty;
                Mode = mode ?? string.Empty;
                ignoredSubscriberIdsText = string.Empty;
            }

            /// <summary>System name as configured in the codeplug.</summary>
            public string SystemName { get; }

            /// <summary>Channel name as configured in the codeplug.</summary>
            public string ChannelName { get; }

            /// <summary>Talkgroup id as configured in the codeplug.</summary>
            public string TalkgroupId { get; }

            /// <summary>Stable per-resource key (<see cref="ResourceIdentity.Build"/>).</summary>
            public string ResourceKey { get; }

            /// <summary>Channel mode, upper-cased for display.</summary>
            public string Mode { get; }

            /// <summary>Whether TAR recording is enabled for this resource.</summary>
            public bool Enabled
            {
                get => enabled;
                set
                {
                    if (enabled == value)
                        return;

                    enabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                }
            }

            /// <summary>Recording retention window in days.</summary>
            public int RetentionDays
            {
                get => retentionDays;
                set
                {
                    if (retentionDays == value)
                        return;

                    retentionDays = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RetentionDays)));
                }
            }

            /// <summary>Comma/semicolon/space-separated ignored subscriber ids; null input becomes empty.</summary>
            public string IgnoredSubscriberIdsText
            {
                get => ignoredSubscriberIdsText;
                set
                {
                    string normalized = value ?? string.Empty;
                    if (ignoredSubscriberIdsText == normalized)
                        return;

                    ignoredSubscriberIdsText = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoredSubscriberIdsText)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        /// <summary>
        /// One zone tab holding its editable channel items.
        /// </summary>
        public sealed class TarZoneConfigGroup
        {
            /// <summary>
            /// Creates a group with the given display name and channel items.
            /// </summary>
            public TarZoneConfigGroup(string zoneName, IEnumerable<TarChannelConfigItem> channels)
            {
                ZoneName = zoneName ?? string.Empty;
                Channels = (channels ?? Enumerable.Empty<TarChannelConfigItem>()).ToList();
            }

            /// <summary>Zone display name ("Tab" for blank zone names).</summary>
            public string ZoneName { get; }

            /// <summary>Editable channel items in codeplug order.</summary>
            public IReadOnlyList<TarChannelConfigItem> Channels { get; }
        }

        private readonly Dictionary<string, List<TarChannelConfigItem>> itemsByResource =
            new Dictionary<string, List<TarChannelConfigItem>>(StringComparer.OrdinalIgnoreCase);

        private bool synchronizingTalkgroupItems;
        private string recordingFolderPath = string.Empty;
        private string statusText = string.Empty;
        private string errorText = string.Empty;

        /// <summary>
        /// Creates the TAR configuration view-model from codeplug zones and an
        /// injected config resolver. The recording folder is the configured path
        /// trimmed of surrounding whitespace, falling back to the trimmed default
        /// when whitespace-only; a whitespace-only default is preserved verbatim so
        /// <see cref="Save"/> rejects it.
        /// </summary>
        /// <param name="zones">Codeplug zones in tab order; null zones and unusable channels are skipped.</param>
        /// <param name="configResolver">Resolves the persisted channel config for a resource key.</param>
        /// <param name="recordingFolderPath">Configured TAR recordings root; whitespace falls back.</param>
        /// <param name="defaultRecordingFolderPath">Fallback recordings root.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configResolver"/> is null.</exception>
        public TarConfigurationViewModel(
            IReadOnlyList<Codeplug.Zone>? zones,
            Func<string, string, string, TarChannelConfig> configResolver,
            string? recordingFolderPath,
            string? defaultRecordingFolderPath)
        {
            if (configResolver == null)
                throw new ArgumentNullException(nameof(configResolver));

            RecordingFolderPath = ResolveRecordingFolderPath(recordingFolderPath, defaultRecordingFolderPath);
            ZoneGroups = BuildZoneGroups(zones, configResolver);
        }

        /// <summary>Zone groups in codeplug order; a lone "Resources" group when nothing is usable.</summary>
        public IReadOnlyList<TarZoneConfigGroup> ZoneGroups { get; }

        /// <summary>Configured TAR recordings root; changes clear prior status/error.</summary>
        public string RecordingFolderPath
        {
            get => recordingFolderPath;
            set
            {
                string normalized = value ?? string.Empty;
                if (recordingFolderPath == normalized)
                    return;

                recordingFolderPath = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingFolderPath)));
                ClearStatus();
            }
        }

        /// <summary>Last save outcome message; empty unless a save succeeded.</summary>
        public string StatusText
        {
            get => statusText;
            private set
            {
                if (statusText == value)
                    return;

                statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        /// <summary>Last validation error; empty unless a save failed.</summary>
        public string ErrorText
        {
            get => errorText;
            private set
            {
                if (errorText == value)
                    return;

                errorText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
            }
        }

        /// <summary>
        /// Raised exactly once on a successful save with the validated recording
        /// root and the per-resource configuration dictionary (case-insensitive
        /// keys, blank keys skipped).
        /// </summary>
        public event Action<string, IReadOnlyDictionary<string, TarChannelConfig>>? SaveRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Validates the recording root, parses every item's ignored subscriber
        /// ids, and emits the save payload. Returns false (without raising
        /// <see cref="SaveRequested"/>) when the root is unusable or any ignored-id
        /// token is invalid; on success the folder path is normalized to the
        /// validated trimmed result and <see cref="StatusText"/> is set.
        /// </summary>
        public bool Save()
        {
            ClearStatus();

            if (!TarRecorder.TryEnsureRecordingRoot(RecordingFolderPath, out string normalizedPath, out string errorMessage))
            {
                ErrorText = errorMessage;
                return false;
            }

            Dictionary<string, TarChannelConfig> configs =
                new Dictionary<string, TarChannelConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (TarChannelConfigItem item in ZoneGroups.SelectMany(group => group.Channels))
            {
                List<uint> ignoredSubscriberIds = new List<uint>();
                if (!TryParseIgnoredSubscriberIds(item.IgnoredSubscriberIdsText, ignoredSubscriberIds, out string parseError))
                {
                    ErrorText = parseError;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(item.ResourceKey))
                    continue;

                configs[item.ResourceKey] = new TarChannelConfig
                {
                    Enabled = item.Enabled,
                    RetentionDays = Math.Max(0, item.RetentionDays),
                    IgnoredSubscriberIds = ignoredSubscriberIds
                };
            }

            if (!string.Equals(RecordingFolderPath, normalizedPath, StringComparison.Ordinal))
                RecordingFolderPath = normalizedPath;

            SaveRequested?.Invoke(normalizedPath, configs);
            StatusText = "Changes saved.";
            return true;
        }

        private static string ResolveRecordingFolderPath(string? recordingFolderPath, string? defaultRecordingFolderPath)
        {
            if (!string.IsNullOrWhiteSpace(recordingFolderPath))
                return recordingFolderPath.Trim();

            return string.IsNullOrWhiteSpace(defaultRecordingFolderPath)
                ? (defaultRecordingFolderPath ?? string.Empty)
                : defaultRecordingFolderPath.Trim();
        }

        private List<TarZoneConfigGroup> BuildZoneGroups(
            IReadOnlyList<Codeplug.Zone>? zones,
            Func<string, string, string, TarChannelConfig> configResolver)
        {
            List<TarZoneConfigGroup> groups = new List<TarZoneConfigGroup>();
            foreach (Codeplug.Zone zone in zones ?? Array.Empty<Codeplug.Zone>())
            {
                if (zone == null)
                    continue;

                List<TarChannelConfigItem> channels = new List<TarChannelConfigItem>();
                foreach (Codeplug.Channel channel in zone.Channels ?? Enumerable.Empty<Codeplug.Channel>())
                {
                    if (channel == null || string.IsNullOrWhiteSpace(channel.Name) || string.IsNullOrWhiteSpace(channel.Tgid))
                        continue;

                    string resourceKey = ResourceIdentity.Build(channel.System, channel.Tgid);
                    TarChannelConfig config =
                        NormalizeLoadedConfig(configResolver(resourceKey, channel.Name, channel.Tgid) ?? new TarChannelConfig());

                    TarChannelConfigItem item = new TarChannelConfigItem(
                        channel.System ?? string.Empty,
                        channel.Name ?? string.Empty,
                        channel.Tgid ?? string.Empty,
                        resourceKey,
                        (channel.Mode ?? string.Empty).ToUpperInvariant())
                    {
                        Enabled = config.Enabled,
                        RetentionDays = config.RetentionDays,
                        IgnoredSubscriberIdsText = string.Join(", ", config.IgnoredSubscriberIds)
                    };

                    item.PropertyChanged += TarChannelConfigItem_PropertyChanged;
                    RegisterResourceItem(item);
                    channels.Add(item);
                }

                // Zones without a single usable channel are dropped; the
                // caller adds the "Resources" placeholder when nothing remains.
                if (channels.Count > 0)
                {
                    groups.Add(new TarZoneConfigGroup(
                        string.IsNullOrWhiteSpace(zone.Name) ? "Tab" : zone.Name.Trim(),
                        channels));
                }
            }

            if (groups.Count == 0)
                groups.Add(new TarZoneConfigGroup("Resources", Enumerable.Empty<TarChannelConfigItem>()));

            return groups;
        }

        /// <summary>
        /// Normalizes a loaded config like the WPF SettingsManager: the legacy
        /// disabled/30-day/empty default becomes 7 days, negative retention
        /// clamps to 0, and ignored ids are filtered (id &gt; 0), deduplicated,
        /// and sorted ascending.
        /// </summary>
        private static TarChannelConfig NormalizeLoadedConfig(TarChannelConfig config)
        {
            bool looksLikeLegacyDefault = config != null &&
                !config.Enabled &&
                config.RetentionDays == 30 &&
                (config.IgnoredSubscriberIds == null || config.IgnoredSubscriberIds.Count == 0);

            int retentionDays = looksLikeLegacyDefault ? 7 : (config?.RetentionDays ?? 7);
            if (retentionDays < 0)
                retentionDays = 0;

            return new TarChannelConfig
            {
                Enabled = config?.Enabled ?? false,
                RetentionDays = retentionDays,
                IgnoredSubscriberIds = (config?.IgnoredSubscriberIds ?? new List<uint>())
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList()
            };
        }

        private void RegisterResourceItem(TarChannelConfigItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ResourceKey))
                return;

            if (!itemsByResource.TryGetValue(item.ResourceKey, out List<TarChannelConfigItem>? groupedItems))
            {
                groupedItems = new List<TarChannelConfigItem>();
                itemsByResource[item.ResourceKey] = groupedItems;
            }

            groupedItems.Add(item);
        }

        private void TarChannelConfigItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            ClearStatus();

            if (synchronizingTalkgroupItems ||
                sender is not TarChannelConfigItem changedItem ||
                string.IsNullOrWhiteSpace(changedItem.ResourceKey) ||
                !itemsByResource.TryGetValue(changedItem.ResourceKey, out List<TarChannelConfigItem>? groupedItems) ||
                groupedItems.Count <= 1)
                return;

            synchronizingTalkgroupItems = true;
            try
            {
                foreach (TarChannelConfigItem item in groupedItems)
                {
                    if (ReferenceEquals(item, changedItem))
                        continue;

                    switch (e.PropertyName)
                    {
                        case nameof(TarChannelConfigItem.Enabled):
                            item.Enabled = changedItem.Enabled;
                            break;
                        case nameof(TarChannelConfigItem.RetentionDays):
                            item.RetentionDays = changedItem.RetentionDays;
                            break;
                        case nameof(TarChannelConfigItem.IgnoredSubscriberIdsText):
                            item.IgnoredSubscriberIdsText = changedItem.IgnoredSubscriberIdsText;
                            break;
                    }
                }
            }
            finally
            {
                synchronizingTalkgroupItems = false;
            }
        }

        private void ClearStatus()
        {
            StatusText = string.Empty;
            ErrorText = string.Empty;
        }

        private static bool TryParseIgnoredSubscriberIds(string text, List<uint> output, out string errorMessage)
        {
            errorMessage = string.Empty;
            output.Clear();

            if (string.IsNullOrWhiteSpace(text))
                return true;

            string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (!uint.TryParse(part.Trim(), out uint subscriberId) || subscriberId == 0)
                {
                    errorMessage = $"'{part}' is not a valid subscriber ID.";
                    return false;
                }

                if (!output.Contains(subscriberId))
                    output.Add(subscriberId);
            }

            output.Sort();
            return true;
        }
    }
}
