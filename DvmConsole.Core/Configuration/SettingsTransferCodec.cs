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
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dvmconsole
{
    /// <summary>
    /// Portable settings-transfer codec: file serialization and IO, category
    /// resolution, payload building, and token conversion. Headless and
    /// POCO-only: no WPF types, no fnecore, no SettingsManager; payloads are
    /// treated as JObject/object/Type.
    /// </summary>
    public static class SettingsTransferCodec
    {
        /// <summary>
        /// Message used when none of the selected import categories exist in
        /// a transfer file.
        /// </summary>
        public const string NO_CATEGORIES_RESOLVED_MESSAGE = "None of the selected categories exist in this transfer file.";

        /// <summary>
        /// Resolves selected category ids against the given definitions.
        /// Ids are trimmed and matched case-insensitively; results always
        /// follow definition order. Empty/blank/unknown selections yield an
        /// empty list.
        /// </summary>
        /// <param name="definitions">All known category definitions in definition order.</param>
        /// <param name="categoryIds">Selected category ids; null or blank ids are ignored.</param>
        /// <returns>Matching definitions in definition order.</returns>
        public static List<SettingsTransferCategoryDefinition> ResolveCategories(
            IEnumerable<SettingsTransferCategoryDefinition> definitions,
            IEnumerable<string> categoryIds)
        {
            List<SettingsTransferCategoryDefinition> result = new List<SettingsTransferCategoryDefinition>();

            HashSet<string> selectedIds = new HashSet<string>(
                categoryIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (selectedIds.Count == 0)
                return result;

            foreach (SettingsTransferCategoryDefinition category in definitions)
            {
                if (selectedIds.Contains(category.Id))
                    result.Add(category);
            }

            return result;
        }

        /// <summary>
        /// Builds a settings payload from the readable properties of the
        /// given source object. Property names are case-insensitively
        /// distinct in first-occurrence order; missing or unreadable names
        /// are skipped; null values are preserved as null tokens.
        /// </summary>
        /// <param name="source">Object whose readable properties are read.</param>
        /// <param name="propertyNames">Candidate property names.</param>
        /// <returns>Payload object keyed by resolved property name.</returns>
        public static JObject BuildPayload(object source, IEnumerable<string> propertyNames)
        {
            JObject payload = new JObject();

            foreach (string propertyName in propertyNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                PropertyInfo property = source.GetType().GetProperty(propertyName);
                if (property == null || !property.CanRead)
                    continue;

                object value = property.GetValue(source);
                payload[propertyName] = value == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(value);
            }

            return payload;
        }

        /// <summary>
        /// Serializes a transfer file to the canonical indented JSON form.
        /// </summary>
        /// <param name="transferFile">Transfer file to serialize.</param>
        /// <returns>Indented JSON text.</returns>
        public static string Serialize(SettingsTransferFile transferFile)
        {
            return JsonConvert.SerializeObject(transferFile, Formatting.Indented);
        }

        /// <summary>
        /// Deserializes a transfer file from JSON text without validation.
        /// </summary>
        /// <param name="json">Transfer file JSON text.</param>
        /// <returns>Deserialized transfer file; null when the text is "null".</returns>
        public static SettingsTransferFile Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<SettingsTransferFile>(json);
        }

        /// <summary>
        /// Writes a transfer file to disk, creating the parent directory
        /// chain when needed.
        /// </summary>
        /// <param name="transferFile">Transfer file to write.</param>
        /// <param name="filePath">Destination path.</param>
        public static void WriteFile(SettingsTransferFile transferFile, string filePath)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(filePath, Serialize(transferFile));
        }

        /// <summary>
        /// Reads and validates a transfer file from disk. The format
        /// identifier is checked case-insensitively; Version is written but
        /// never validated.
        /// </summary>
        /// <param name="filePath">Path to the transfer file.</param>
        /// <returns>Validated transfer file.</returns>
        /// <exception cref="ArgumentException">Path is blank.</exception>
        /// <exception cref="FileNotFoundException">File does not exist.</exception>
        /// <exception cref="InvalidOperationException">File content is not a valid or matching transfer file.</exception>
        public static SettingsTransferFile ReadFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Import path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Settings transfer file was not found.", filePath);

            SettingsTransferFile transferFile = Deserialize(File.ReadAllText(filePath));
            if (transferFile == null || transferFile.Settings == null)
                throw new InvalidOperationException("The selected file is not a valid settings transfer file.");
            if (!string.Equals(transferFile.Format, SettingsTransferFile.FORMAT, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected file is not a dvmconsole settings transfer file.");

            return transferFile;
        }

        /// <summary>
        /// Converts a JSON token to the target property type: null tokens
        /// become null for reference/nullable types and the default instance
        /// for non-nullable value types; everything else uses Newtonsoft
        /// conversion without TypeNameHandling or custom converters.
        /// </summary>
        /// <param name="token">JSON token to convert.</param>
        /// <param name="targetType">Target CLR type.</param>
        /// <returns>Converted value.</returns>
        public static object ConvertToken(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                Type nullableType = Nullable.GetUnderlyingType(targetType);
                if (!targetType.IsValueType || nullableType != null)
                    return null;

                return Activator.CreateInstance(targetType);
            }

            return token.ToObject(targetType);
        }
    }
}
