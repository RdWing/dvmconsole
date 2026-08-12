// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core groups and patches settings
    /// section. The DTO owns WPF-compatible JSON names, defaults, and ordered
    /// member values; this adapter performs no normalization or runtime group
    /// application.
    /// <para>
    /// Group editor UI, PatchManager composition, patch/multi-select PTT, and
    /// startup application belong to later groups/runtime slices.
    /// </para>
    /// </summary>
    public sealed class GroupSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="GroupSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        public GroupSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the groups section through Core. Missing,
        /// malformed, non-object, and unreadable files return false with fresh
        /// empty maps and never throw.
        /// </summary>
        public bool TryLoad(out UserSettingsGroupSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves the groups section through Core, preserving unrelated settings
        /// properties and propagating malformed-file failures.
        /// </summary>
        public void Save(UserSettingsGroupSection section)
            => store.SaveSection(section);
    }
}
