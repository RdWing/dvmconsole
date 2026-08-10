// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core
    /// <see cref="SettingsSectionStore"/> for the PTT-settings section
    /// (<see cref="UserSettingsPttSection"/>).
    /// The section DTO and store live in Core; the DTO owns the
    /// WPF-compatible defaults and values, so this adapter performs no
    /// normalization, validation, or key-mapping of its own.
    /// <see cref="UserSettingsPttSection.TogglePTTMode"/>,
    /// <see cref="UserSettingsPttSection.GlobalPTTShortcut"/>, and
    /// <see cref="UserSettingsPttSection.GlobalPTTKeysAllChannels"/> round-trip
    /// verbatim through the store, preserving the store's merge and
    /// error semantics.
    /// <para>
    /// <see cref="UserSettingsPttSection.GlobalPTTShortcut"/> is persisted as
    /// the raw WPF <c>System.Windows.Forms.Keys</c> virtual-key integer; zero
    /// means "no shortcut". Mapping that integer to platform
    /// <c>HotkeyGesture</c> values is a later shell boundary (the future
    /// editor/view-model and KeyGestureMapper seam), not part of this
    /// adapter — no raw-key-to-gesture conversion happens here.
    /// </para>
    /// <para>
    /// Headless only: PTT hotkey registration, dispatcher/UI work,
    /// MainWindow/shell wiring, and gesture mapping are later seams, not
    /// part of this adapter.
    /// </para>
    /// </summary>
    public sealed class PttSettingsPersistence
    {
        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="PttSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="store"/> is null.</exception>
        public PttSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the PTT settings section through the Core
        /// store. Returns true with the loaded section when the store file
        /// holds valid section JSON; returns false with the section DTO
        /// defaults (<c>TogglePTTMode</c> false, <c>GlobalPTTShortcut</c>
        /// zero, <c>GlobalPTTKeysAllChannels</c> false) for a missing,
        /// empty, malformed or otherwise unreadable file, without throwing.
        /// </summary>
        public bool TryLoad(out UserSettingsPttSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves the PTT settings section through the Core store,
        /// merging only the section properties and preserving every
        /// unrelated existing property value-for-value.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="section"/> is null.</exception>
        /// <exception cref="Newtonsoft.Json.JsonException">Thrown when an
        /// existing store file is malformed, non-object or unreadable; the
        /// file is never overwritten in that case.</exception>
        public void Save(UserSettingsPttSection section)
            => store.SaveSection(section);
    }
}
