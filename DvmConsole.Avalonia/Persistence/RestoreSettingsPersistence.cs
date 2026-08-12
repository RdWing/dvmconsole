// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core settings-section store for
    /// Gate 3.4 restore-selection state. The adapter performs no hydration or
    /// identity normalization; those rules belong to the dashboard view-model.
    /// </summary>
    public sealed class RestoreSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>Initializes the adapter over one settings file.</summary>
        public RestoreSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>Attempts to load the restore-selection section.</summary>
        public bool TryLoad(out UserSettingsRestoreSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves restore-selection state while preserving unrelated settings.
        /// </summary>
        public void Save(UserSettingsRestoreSection section)
            => store.SaveSection(section);
    }
}
