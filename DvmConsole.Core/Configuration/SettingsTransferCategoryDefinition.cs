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

using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Import/export category shown in the settings transfer window. Owned
    /// by DvmConsole.Core; the concrete category list and property names
    /// remain in the WPF SettingsManager.
    /// </summary>
    public class SettingsTransferCategoryDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> PropertyNames { get; set; } = new List<string>();
    }
}
