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
    /// Resolves the configured TAR recordings root path, centralizing the
    /// duplicated normalization ternary used across the WPF app:
    /// <c>string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()</c>.
    /// <para>
    /// A null, empty or whitespace-only <paramref name="configuredPath"/> returns
    /// <paramref name="fallbackPath"/> unchanged (the same string instance, relative
    /// or absolute). Any non-empty <paramref name="configuredPath"/> is returned
    /// trimmed of surrounding whitespace but otherwise preserved verbatim, including
    /// relative paths and interior whitespace. The helper enforces no rootedness and
    /// performs no filesystem I/O.
    /// </para>
    /// </summary>
    public static class TarRecordingsPath
    {
        /// <summary>
        /// Resolves the configured TAR recordings root path.
        /// </summary>
        /// <param name="configuredPath">Configured recordings root path; null, empty or whitespace-only
        /// values fall back to <paramref name="fallbackPath"/>.</param>
        /// <param name="fallbackPath">Fallback path returned unchanged when <paramref name="configuredPath"/>
        /// is null, empty or whitespace-only.</param>
        /// <returns><paramref name="fallbackPath"/> when <paramref name="configuredPath"/> is null, empty or
        /// whitespace-only; otherwise <paramref name="configuredPath"/> trimmed of surrounding whitespace.</returns>
        public static string Resolve(string configuredPath, string fallbackPath)
            => string.IsNullOrWhiteSpace(configuredPath) ? fallbackPath : configuredPath.Trim();
    } // public static class TarRecordingsPath
}
