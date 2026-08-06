// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Persistence
{
    /// <summary>
    /// Avalonia-facing adapter around the Core
    /// <see cref="SettingsSectionStore"/> for the audio-settings section.
    /// The section DTO and store live in Core; the section-key to
    /// platform-device-id mapping lives here, deliberately outside the
    /// dependency-free Platform assembly.
    /// </summary>
    public sealed class AudioSettingsPersistence
    {
        /// <summary>
        /// The WPF-compatible default-device section key. The platform
        /// default device id maps to exactly this key.
        /// </summary>
        private const string DefaultDeviceKey = "windows-default";

        private readonly SettingsSectionStore store;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioSettingsPersistence"/> class.
        /// </summary>
        /// <param name="store">Core settings-section store bound to the
        /// settings file; must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="store"/> is null.</exception>
        public AudioSettingsPersistence(SettingsSectionStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempts to load the audio settings section through the Core
        /// store. Returns true with the loaded section when the store file
        /// holds valid section JSON; returns false with the section DTO
        /// defaults for a missing, empty, malformed or otherwise unreadable
        /// file, without throwing.
        /// </summary>
        public bool TryLoad(out UserSettingsAudioSection section)
            => store.TryLoadSection(out section);

        /// <summary>
        /// Saves the audio settings section through the Core store,
        /// merging only the section properties and preserving every
        /// unrelated existing property value-for-value.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when
        /// <paramref name="section"/> is null.</exception>
        /// <exception cref="Newtonsoft.Json.JsonException">Thrown when an
        /// existing store file is malformed, non-object or unreadable; the
        /// file is never overwritten in that case.</exception>
        public void Save(UserSettingsAudioSection section)
            => store.SaveSection(section);

        /// <summary>
        /// Maps a persisted section device key to a platform
        /// <see cref="AudioDeviceId"/>. Null, empty, whitespace-only and
        /// case-insensitive <c>windows-default</c> keys map to
        /// <see cref="AudioDeviceId.Default"/>; any other key is trimmed
        /// and wrapped as an opaque non-default id.
        /// </summary>
        public static AudioDeviceId ToAudioDeviceId(string? sectionKey)
        {
            string? key = string.IsNullOrWhiteSpace(sectionKey) ? null : sectionKey.Trim();

            if (key is null
                || string.Equals(key, DefaultDeviceKey, StringComparison.OrdinalIgnoreCase))
            {
                return AudioDeviceId.Default;
            }

            return AudioDeviceId.FromKey(key);
        }

        /// <summary>
        /// Maps a platform <see cref="AudioDeviceId"/> to the persisted
        /// section device key. The default id maps to exactly
        /// <c>windows-default</c>; a non-default id returns its opaque
        /// value verbatim.
        /// </summary>
        public static string ToSettingsKey(AudioDeviceId id)
            => id.IsDefault ? DefaultDeviceKey : id.Value;
    }
}
