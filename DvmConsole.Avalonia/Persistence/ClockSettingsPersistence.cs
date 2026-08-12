// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core toolbar-clock settings section.
    /// The adapter owns only the merge-preserving store boundary; WPF-compatible
    /// clock normalization is performed by <see cref="UserSettingsClockSection.Normalize"/>
    /// at load and save boundaries.
    /// <para>
    /// Runtime clock manager, toolbar strip, timer, formatting and shell wiring
    /// belong to the later clock-manager/strip slice.
    /// </para>
    /// </summary>
    public sealed class ClockSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClockSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        public ClockSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load and normalize the toolbar-clock section. Missing,
        /// malformed, non-object and unreadable files return false with WPF
        /// defaults and never throw.
        /// </summary>
        public bool TryLoad(out UserSettingsClockSection section)
        {
            bool loaded = store.TryLoadSection(out UserSettingsClockSection raw);
            section = UserSettingsClockSection.Normalize(raw);
            return loaded;
        }

        /// <summary>
        /// Normalizes and saves the toolbar-clock section while preserving every
        /// unrelated settings property. Malformed existing files propagate the
        /// store exception without overwriting the file.
        /// </summary>
        public void Save(UserSettingsClockSection section)
            => store.SaveSection(UserSettingsClockSection.NormalizeForSave(section));
    }
}
