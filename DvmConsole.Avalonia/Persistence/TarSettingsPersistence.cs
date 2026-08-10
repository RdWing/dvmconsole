// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing TAR settings persistence adapter: WPF-compatible
    /// normalization at the persistence boundary over the Core
    /// <see cref="SettingsSectionStore"/> merge-preserving store.
    /// <para>
    /// Load and save normalize the TAR recordings root and per-channel config
    /// map exactly like the WPF <c>SettingsManager</c>
    /// (<c>GetTarChannelConfigs</c>/<c>SaveTarSettings</c>,
    /// SettingsManager.cs:1762-1775 and 1806-1824, with
    /// <c>NormalizeTarChannelConfig</c> at 2144-2168): keys are trimmed with
    /// blank keys skipped, the map is case-insensitive, retention legacy
    /// defaults and negatives are normalized, and ignored subscriber ids are
    /// filtered (id &gt; 0), deduplicated, and sorted ascending.
    /// </para>
    /// <para>
    /// All file I/O is delegated to the injected Core
    /// <see cref="SettingsSectionStore"/>, which owns the merge-preserving
    /// JSON read/update/write of the whole settings file: saving this section
    /// updates only the TAR properties and preserves every unrelated property
    /// value-for-value. This adapter is headless (no UI, dispatcher, dialogs,
    /// <c>SettingsManager</c>, WPF, NAudio, or Platform references) and is
    /// signature-compatible with
    /// <c>TarConfigurationViewModel.SaveRequested</c>.
    /// </para>
    /// </summary>
    public sealed class TarSettingsPersistence
    {
        /// <summary>
        /// The WPF-compatible default TAR recordings root
        /// (<c>SettingsManager.DefaultTarRecordingsPath</c>:
        /// Documents\DVMConsole\TAR), used whenever the configured root is
        /// null, empty or whitespace-only.
        /// </summary>
        private static readonly string DefaultRecordingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DVMConsole",
            "TAR");

        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="TarSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="store"/> is null.</exception>
        public TarSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the TAR settings section through the Core store.
        /// Returns true with the loaded section when the store file holds valid
        /// section JSON; returns false with the section DTO defaults for a
        /// missing, empty, malformed or otherwise unreadable file, without
        /// throwing. The returned section is always normalized: the recordings
        /// root is resolved like the WPF ternary
        /// (<see cref="TarRecordingsPath.Resolve"/> with
        /// <see cref="DefaultRecordingPath"/> as fallback) and the config map
        /// is normalized exactly like
        /// <c>SettingsManager.GetTarChannelConfigs</c>.
        /// </summary>
        public bool TryLoad(out UserSettingsTarSection section)
        {
            bool loaded = store.TryLoadSection(out section);

            section.TarRecordingsRootPath =
                TarRecordingsPath.Resolve(section.TarRecordingsRootPath, DefaultRecordingPath);
            section.TarChannelConfigs = NormalizeConfigMap(section.TarChannelConfigs);

            return loaded;
        }

        /// <summary>
        /// Saves the TAR recordings root and per-channel config map through the
        /// Core store, which merges only the TAR section properties and
        /// preserves every unrelated existing setting value-for-value. The
        /// configured root is resolved like the WPF ternary (trimmed when
        /// non-blank, otherwise <see cref="DefaultRecordingPath"/>); keys and
        /// configs are normalized like <c>SettingsManager.SaveTarSettings</c>
        /// into a fresh case-insensitive dictionary holding fresh
        /// <see cref="TarChannelConfig"/> instances, so no caller-supplied
        /// dictionary or list reference is retained.
        /// </summary>
        /// <param name="recordingFolderPath">Configured TAR recordings root;
        /// null, empty or whitespace-only falls back to the default.</param>
        /// <param name="configs">Per-channel TAR configs keyed by talkgroup
        /// id; null, blank keys, and null entries are handled.</param>
        /// <exception cref="Newtonsoft.Json.JsonException">Thrown when an
        /// existing store file is malformed, non-object or unreadable; the
        /// file is never overwritten in that case.</exception>
        public void Save(string? recordingFolderPath, IReadOnlyDictionary<string, TarChannelConfig>? configs)
        {
            store.SaveSection(new UserSettingsTarSection
            {
                TarRecordingsRootPath =
                    TarRecordingsPath.Resolve(recordingFolderPath, DefaultRecordingPath),
                TarChannelConfigs = NormalizeConfigMap(configs)
            });
        }

        /// <summary>
        /// Normalizes a loaded or caller-supplied config map into a fresh
        /// case-insensitive dictionary: keys are trimmed with blank keys
        /// skipped, and every value is normalized through
        /// <see cref="NormalizeConfig"/> (null values become WPF defaults).
        /// </summary>
        private static Dictionary<string, TarChannelConfig> NormalizeConfigMap(
            IReadOnlyDictionary<string, TarChannelConfig>? configs)
        {
            Dictionary<string, TarChannelConfig> normalized =
                new Dictionary<string, TarChannelConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, TarChannelConfig> kvp in configs ?? new Dictionary<string, TarChannelConfig>())
            {
                string? configKey = kvp.Key?.Trim();
                if (string.IsNullOrWhiteSpace(configKey))
                    continue;

                normalized[configKey] = NormalizeConfig(kvp.Value);
            }

            return normalized;
        }

        /// <summary>
        /// Normalizes one channel config exactly like the WPF
        /// <c>SettingsManager.NormalizeTarChannelConfig</c>: null becomes the
        /// WPF default (Enabled false, RetentionDays 7); the legacy disabled
        /// 30-day default becomes 7 days only when the ignored subscriber id
        /// list is null or empty; negative retention clamps to 0; and
        /// ignored subscriber ids are filtered (id &gt; 0), deduplicated, and
        /// sorted ascending. Always returns a fresh
        /// <see cref="TarChannelConfig"/> with a fresh id list.
        /// </summary>
        private static TarChannelConfig NormalizeConfig(TarChannelConfig? config)
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
    }
}
