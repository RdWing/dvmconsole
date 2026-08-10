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

using System;
using System.Collections.Generic;
using System.IO;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned TAR recording settings section DTO, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names, JSON shape, and
    /// defaults stay compatible with the WPF SettingsManager TAR properties
    /// (SettingsManager.cs:199-207); values are never normalized, validated,
    /// or reordered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Top-level JSON keys are the WPF-compatible PascalCase names
    /// <c>TarRecordingsRootPath</c> and <c>TarChannelConfigs</c>, with
    /// <c>TarChannelConfigs</c> keyed by WPF talkgroup ID strings (e.g.
    /// <c>sys|1</c>). Path trimming and per-channel config normalization
    /// (SettingsManager.SaveTarSettings, SettingsManager.cs:1806-1820)
    /// belong to later adapter/view-model seams, not to this data-only DTO.
    /// </para>
    /// <para>
    /// <see cref="TarChannelConfigs"/> reuses the shared portable
    /// <see cref="TarChannelConfig"/> type (TarModels.cs) as-is; JSON
    /// compatibility depends on the object shape, not the CLR type name.
    /// </para>
    /// </remarks>
    public sealed class UserSettingsTarSection
    {
        /// <summary>
        /// Root folder where TAR recordings are stored (WPF
        /// SettingsManager.DefaultTarRecordingsPath: Documents\DVMConsole\TAR).
        /// </summary>
        public string TarRecordingsRootPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DVMConsole",
            "TAR");

        /// <summary>
        /// Per-channel TAR recording configs keyed by talkgroup ID (WPF
        /// SettingsManager.TarChannelConfigs); compared OrdinalIgnoreCase like
        /// the WPF SaveTarSettings normalization dictionary.
        /// </summary>
        public Dictionary<string, TarChannelConfig> TarChannelConfigs { get; set; } =
            new Dictionary<string, TarChannelConfig>(StringComparer.OrdinalIgnoreCase);
    }
}
