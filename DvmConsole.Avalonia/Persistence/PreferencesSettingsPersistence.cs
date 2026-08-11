// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core
    /// <see cref="SettingsSectionStore"/> for the operator-preferences
    /// section (<see cref="UserSettingsPreferencesSection"/>).
    /// The section DTO owns WPF-compatible property names and defaults, so
    /// this adapter performs no normalization or runtime application.
    /// <para>
    /// Missing, malformed, non-object, and unreadable files return false with
    /// a fresh DTO containing the six false defaults. Saves delegate directly
    /// to the merge-preserving store: unrelated settings survive, while a
    /// malformed existing file throws and is never overwritten.
    /// </para>
    /// <para>
    /// Headless only: menu controls, view-model wiring, dispatcher work,
    /// permit-tone/RX-mute application, selection restoration, theme, and
    /// always-on-top behavior belong to later preference gates.
    /// </para>
    /// </summary>
    public sealed class PreferencesSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="PreferencesSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="store"/> is null.</exception>
        public PreferencesSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the operator-preferences section through Core.
        /// </summary>
        public bool TryLoad(out UserSettingsPreferencesSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves the operator-preferences section through Core, preserving
        /// unrelated properties and propagating malformed-file failures.
        /// </summary>
        public void Save(UserSettingsPreferencesSection section)
            => store.SaveSection(section);
    }
}
