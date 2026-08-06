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
#nullable enable
using System;
using System.IO;
using dvmconsole;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration
{
    /// <summary>
    /// Headless codeplug loader: parses a dvmconsole codeplug YAML
    /// document with the exact production deserializer configuration the
    /// WPF app uses (MainWindow.xaml.cs LoadCodeplug) — CamelCase naming
    /// convention, unmatched properties ignored, then
    /// <see cref="Codeplug.NormalizeGroups"/> — but NEVER throws. Missing
    /// files, malformed YAML, and empty documents all produce a typed
    /// <see cref="CodeplugLoadResult"/> (FileMissing / failed with an
    /// ErrorMessage); only a successful parse yields a Codeplug.
    /// <see cref="LoadFromText"/> is the headless test seam;
    /// <see cref="LoadFromFile"/> wraps it with a filesystem probe.
    /// </summary>
    public sealed class CodeplugLoader
    {
        /// <summary>
        /// Shared deserializer built with the WPF production
        /// configuration. YamlDotNet deserializers are thread-safe, so a
        /// single instance is safe for every concurrent load.
        /// </summary>
        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        /// <summary>
        /// Parses the given codeplug YAML text with the WPF production
        /// deserializer configuration and normalizes the group lists.
        /// Null, empty, or whitespace-only text fails with an error
        /// message; YAML documents that deserialize to a null Codeplug
        /// (e.g. the scalars "null", "~", or comment-only text) are
        /// likewise treated as failures; malformed YAML and
        /// deserialization failures are caught and reported in the
        /// result. Never throws.
        /// </summary>
        /// <param name="yaml">Codeplug YAML document text.</param>
        /// <returns>The typed load outcome.</returns>
        public static CodeplugLoadResult LoadFromText(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return CodeplugLoadResult.Failed("codeplug text is empty");
            }

            try
            {
                Codeplug codeplug = Deserializer.Deserialize<Codeplug>(yaml);
                if (codeplug is null)
                {
                    return CodeplugLoadResult.Failed("codeplug document is empty");
                }

                codeplug.NormalizeGroups();
                return CodeplugLoadResult.Success(codeplug);
            }
            catch (Exception ex)
            {
                return CodeplugLoadResult.Failed("codeplug parse failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Loads and parses the codeplug YAML file at the given path,
        /// delegating to <see cref="LoadFromText"/>. A null or blank path
        /// and a missing file both yield a FileMissing result; read and
        /// parse failures yield a failed result with an error message.
        /// Never throws.
        /// </summary>
        /// <param name="filePath">Path to the codeplug YAML file.</param>
        /// <returns>The typed load outcome.</returns>
        public static CodeplugLoadResult LoadFromFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return CodeplugLoadResult.NotFound(filePath);
            }

            try
            {
                return LoadFromText(File.ReadAllText(filePath));
            }
            catch (Exception ex)
            {
                return CodeplugLoadResult.Failed("codeplug read failed: " + ex.Message);
            }
        }
    }
}
