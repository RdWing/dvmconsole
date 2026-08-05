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

namespace dvmconsole
{
    /// <summary>
    /// Contract for the on-disk file system paths the console depends on.
    /// Implementations must expose immutable, fully composed absolute paths.
    /// </summary>
    public interface IFileSystemPaths
    {
        /// <summary>
        /// Root folder for application data (settings, trace logs, aliases).
        /// </summary>
        string ApplicationDataRootPath { get; }

        /// <summary>
        /// Full path to the UserSettings.json settings file.
        /// </summary>
        string SettingsFilePath { get; }

        /// <summary>
        /// Default root folder where TAR recordings are stored.
        /// </summary>
        string DefaultTarRecordingsPath { get; }

        /// <summary>
        /// Directory where trace/debug logs are written; equals the application root.
        /// </summary>
        string TraceLogDirectoryPath { get; }

        /// <summary>
        /// Directory where alias files live; equals the application root.
        /// </summary>
        string DefaultAliasDirectoryPath { get; }
    } // public interface IFileSystemPaths
}
