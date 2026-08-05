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
    /// Default <see cref="IFileSystemPaths"/> implementation.
    /// <para>
    /// Paths are composed once in the constructor with <see cref="System.IO.Path.Combine"/>
    /// and are immutable afterwards. An empty or null base falls back to the
    /// real environment folder (ApplicationData / MyDocuments). A non-empty
    /// <paramref name="userProfilePathOverride"/> is used verbatim as the
    /// application root, preserving the App.USER_PROFILE_PATH_OVERRIDE
    /// semantics (string.Empty means no override) and never affecting the TAR
    /// recordings path.
    /// </para>
    /// </summary>
    public sealed class DefaultFileSystemPaths : IFileSystemPaths
    {
        /// <summary>
        /// Root folder for application data (settings, trace logs, aliases).
        /// </summary>
        public string ApplicationDataRootPath { get; }

        /// <summary>
        /// Full path to the UserSettings.json settings file.
        /// </summary>
        public string SettingsFilePath { get; }

        /// <summary>
        /// Default root folder where TAR recordings are stored.
        /// </summary>
        public string DefaultTarRecordingsPath { get; }

        /// <summary>
        /// Directory where trace/debug logs are written; equals the application root.
        /// </summary>
        public string TraceLogDirectoryPath { get; }

        /// <summary>
        /// Directory where alias files live; equals the application root.
        /// </summary>
        public string DefaultAliasDirectoryPath { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultFileSystemPaths"/> class.
        /// </summary>
        /// <param name="applicationDataBasePath">Base folder for application data; when null or empty the
        /// real ApplicationData environment folder is used (falling back to the user profile folder if
        /// that is unavailable).</param>
        /// <param name="documentsBasePath">Base folder for documents; when null or empty the real
        /// MyDocuments environment folder is used (falling back to the user profile folder if that is
        /// unavailable).</param>
        /// <param name="userProfilePathOverride">When non-empty, used verbatim as the application root
        /// (App.USER_PROFILE_PATH_OVERRIDE semantics); string.Empty or null means no override.</param>
        public DefaultFileSystemPaths(string applicationDataBasePath = null, string documentsBasePath = null, string userProfilePathOverride = null)
        {
            string appDataBase = string.IsNullOrEmpty(applicationDataBasePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : applicationDataBasePath;

            string docsBase = string.IsNullOrEmpty(documentsBasePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : documentsBasePath;

            // Some environments (e.g. Linux without an XDG Documents folder)
            // report an empty string for these folders; an empty base would
            // silently yield a relative path, so fall back to the always-rooted
            // user profile folder instead.
            if (string.IsNullOrEmpty(appDataBase))
                appDataBase = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrEmpty(docsBase))
                docsBase = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            bool hasOverride = !string.IsNullOrEmpty(userProfilePathOverride);

            ApplicationDataRootPath = hasOverride
                ? userProfilePathOverride
                : Path.Combine(appDataBase, "DVMProject", "dvmconsole");

            SettingsFilePath = Path.Combine(ApplicationDataRootPath, "UserSettings.json");
            DefaultTarRecordingsPath = Path.Combine(docsBase, "DVMConsole", "TAR");
            TraceLogDirectoryPath = ApplicationDataRootPath;
            DefaultAliasDirectoryPath = ApplicationDataRootPath;
        } // public DefaultFileSystemPaths(...)
    } // public sealed class DefaultFileSystemPaths
}
