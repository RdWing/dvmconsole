// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing merge-preserving persistence adapter for TAR Viewer
    /// column visibility. The Core store owns whole-file JSON merging; this
    /// adapter owns only key normalization and the named viewer section.
    /// </summary>
    public sealed class TarViewerColumnSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>Creates the adapter over one shared settings store.</summary>
        public TarViewerColumnSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Loads the viewer section without throwing for missing or malformed
        /// files. Keys are trimmed, blank keys are dropped, and a fresh
        /// case-insensitive dictionary is returned.
        /// </summary>
        public bool TryLoad(out UserSettingsTarViewerSection section)
        {
            bool loaded = store.TryLoadSection(out section);
            section.ColumnVisibility = Normalize(section.ColumnVisibility);
            return loaded;
        }

        /// <summary>
        /// Saves only the viewer section while preserving unrelated settings.
        /// Caller-owned dictionary references are never retained.
        /// </summary>
        public void Save(IReadOnlyDictionary<string, bool>? visibility)
        {
            store.SaveSection(new UserSettingsTarViewerSection
            {
                ColumnVisibility = Normalize(visibility)
            });
        }

        private static Dictionary<string, bool> Normalize(
            IReadOnlyDictionary<string, bool>? visibility)
        {
            var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, bool> entry in
                visibility ?? new Dictionary<string, bool>())
            {
                string key = entry.Key?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                normalized[key] = entry.Value;
            }

            return normalized;
        }
    }
}
