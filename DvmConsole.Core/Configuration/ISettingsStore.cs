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
    /// Contract for a generic, file-backed settings store.
    /// Implementations bind to one immutable file path at construction and
    /// serialize whole settings objects as JSON.
    /// </summary>
    public interface ISettingsStore
    {
        /// <summary>
        /// True when the backing settings file currently exists on disk.
        /// </summary>
        bool Exists { get; }

        /// <summary>
        /// Attempts to load the settings file into <typeparamref name="T"/>.
        /// Returns true with a non-null <paramref name="settings"/> on success;
        /// returns false with a null <paramref name="settings"/> when the file
        /// is missing, empty, malformed or otherwise unreadable, without
        /// throwing.
        /// </summary>
        bool TryLoad<T>(out T settings) where T : class;

        /// <summary>
        /// Serializes <paramref name="settings"/> to indented JSON and writes
        /// it to the store path, creating the parent directory when needed.
        /// </summary>
        void Save<T>(T settings);

        /// <summary>
        /// Deletes the settings file; a no-op when no file exists.
        /// </summary>
        void Delete();
    } // public interface ISettingsStore
}
