// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core
    /// <see cref="SettingsSectionStore"/> for the layout-settings section.
    /// The section DTO and store live in Core; the DTO owns the
    /// WPF-compatible defaults and values, so this adapter performs no
    /// normalization or key mapping of its own.
    /// <para>
    /// Headless only: startup window-restore and close-window save wiring
    /// (including the WPF <c>Maximized</c> flag and window position maps)
    /// is a later shell gate, not part of this adapter.
    /// </para>
    /// </summary>
    public sealed class LayoutSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="LayoutSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="store"/> is null.</exception>
        public LayoutSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the layout settings section through the Core
        /// store. Returns true with the loaded section when the store file
        /// holds valid section JSON; returns false with the section DTO
        /// defaults for a missing, empty, malformed or otherwise unreadable
        /// file, without throwing.
        /// </summary>
        public bool TryLoad(out UserSettingsLayoutSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves the layout settings section through the Core store,
        /// merging only the section properties and preserving every
        /// unrelated existing property value-for-value.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="section"/> is null.</exception>
        /// <exception cref="Newtonsoft.Json.JsonException">Thrown when an
        /// existing store file is malformed, non-object or unreadable; the
        /// file is never overwritten in that case.</exception>
        public void Save(UserSettingsLayoutSection section)
            => store.SaveSection(section);
    }
}
