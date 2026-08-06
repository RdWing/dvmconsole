// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dvmconsole
{
    /// <summary>
    /// Merge-preserving settings-section store bound to one immutable file path.
    /// <para>
    /// <see cref="SaveSection{T}(T)"/> reads an existing file as a
    /// <see cref="JObject"/> first and updates only the serialized section
    /// properties, leaving every unrelated property untouched; a missing file
    /// produces a fresh object containing only the section properties. Files are
    /// written indented as UTF-8 without a BOM.
    /// </para>
    /// </summary>
    public sealed class SettingsSectionStore
    {
        private readonly string filePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsSectionStore"/> class.
        /// </summary>
        /// <param name="filePath">Full path to the settings file. Null, empty or
        /// whitespace is rejected.</param>
        /// <exception cref="System.ArgumentException">Thrown when
        /// <paramref name="filePath"/> is null, empty or whitespace.</exception>
        public SettingsSectionStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A full file path is required.", nameof(filePath));

            this.filePath = filePath;
        } // public SettingsSectionStore(...)

        /// <summary>
        /// Attempts to load the section from the store file into
        /// <typeparamref name="T"/>. Returns true with the loaded section when the
        /// file holds a valid JSON object; returns false with a fresh
        /// <typeparamref name="T"/> (DTO defaults) for a missing, empty, malformed,
        /// non-object or otherwise unreadable file, without throwing.
        /// </summary>
        public bool TryLoadSection<T>(out T section) where T : class, new()
        {
            section = new T();
            try
            {
                if (!File.Exists(filePath))
                    return false;

                string json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                    return false;

                JToken token = JToken.Parse(json);
                if (token is not JObject)
                    return false;

                section = token.ToObject<T>();
                return true;
            }
            catch
            {
                section = new T();
                return false;
            }
        } // public bool TryLoadSection<T>(...)

        /// <summary>
        /// Serializes <paramref name="section"/> to indented JSON and merges its
        /// properties into the store file, preserving every unrelated existing
        /// property value-for-value. Creates the parent directory when needed.
        /// Malformed, non-object or unreadable existing files throw and are never
        /// overwritten.
        /// </summary>
        public void SaveSection<T>(T section) where T : class
        {
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            JObject root;
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                JToken token = JToken.Parse(json);
                if (token is not JObject existing)
                    throw new JsonException("The settings file does not contain a JSON object.");

                root = existing;
            }
            else
            {
                root = new JObject();
            }

            JObject sectionJson = JObject.FromObject(section);
            foreach (JProperty property in sectionJson.Properties())
            {
                root[property.Name] = property.Value;
            }

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, root.ToString(Formatting.Indented));
        } // public void SaveSection<T>(...)
    } // public sealed class SettingsSectionStore
}
