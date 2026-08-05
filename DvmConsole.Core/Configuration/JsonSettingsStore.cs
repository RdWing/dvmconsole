// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024-2025 Caleb, K4PHP
*   Copyright (C) 2025 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2025 Steven Jennison, KD8RHO
*   Copyright (C) 2025 Lorenzo L Romero, K2LLR
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

using Newtonsoft.Json;

namespace dvmconsole
{
    /// <summary>
    /// Default <see cref="ISettingsStore"/> implementation: a whole-object
    /// JSON store bound to one immutable file path.
    /// <para>
    /// Saves serialize with <see cref="JsonConvert.SerializeObject(object, Formatting)"/>
    /// using <see cref="Formatting.Indented"/> (PascalCase, no
    /// TypeNameHandling) and write with
    /// <see cref="System.IO.File.WriteAllText(string, string)"/> (UTF-8 without
    /// a BOM, non-atomic overwrite). Loads read with
    /// <see cref="System.IO.File.ReadAllText(string)"/> and deserialize with
    /// default settings; any failure yields false with a null out value.
    /// </para>
    /// </summary>
    public sealed class JsonSettingsStore : ISettingsStore
    {
        private readonly string filePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonSettingsStore"/> class.
        /// </summary>
        /// <param name="filePath">Full path to the settings file. Null, empty or
        /// whitespace is rejected.</param>
        /// <exception cref="System.ArgumentException">Thrown when
        /// <paramref name="filePath"/> is null, empty or whitespace.</exception>
        public JsonSettingsStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A full file path is required.", nameof(filePath));

            this.filePath = filePath;
        } // public JsonSettingsStore(...)

        /// <summary>
        /// True when the backing settings file currently exists on disk.
        /// </summary>
        public bool Exists
        {
            get { return File.Exists(filePath); }
        } // public bool Exists

        /// <summary>
        /// Attempts to load the settings file into <typeparamref name="T"/>.
        /// Returns true with a non-null <paramref name="settings"/> on success;
        /// returns false with a null <paramref name="settings"/> for a missing,
        /// empty, malformed, null-valued or otherwise unreadable file, without
        /// throwing.
        /// </summary>
        public bool TryLoad<T>(out T settings) where T : class
        {
            settings = null;
            try
            {
                if (!File.Exists(filePath))
                    return false;

                string json = File.ReadAllText(filePath);
                settings = JsonConvert.DeserializeObject<T>(json);
                return settings != null;
            }
            catch
            {
                settings = null;
                return false;
            }
        } // public bool TryLoad<T>(...)

        /// <summary>
        /// Serializes <paramref name="settings"/> to indented JSON and writes
        /// it to the store path, creating the parent directory when needed.
        /// I/O and serialization failures propagate.
        /// </summary>
        public void Save<T>(T settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(filePath, json);
        } // public void Save<T>(...)

        /// <summary>
        /// Deletes the settings file; a no-op when no file exists.
        /// </summary>
        public void Delete()
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        } // public void Delete()
    } // public sealed class JsonSettingsStore
}
