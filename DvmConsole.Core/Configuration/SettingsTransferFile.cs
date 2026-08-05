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

using Newtonsoft.Json.Linq;

namespace dvmconsole
{
    /// <summary>
    /// Portable settings transfer file DTO: format header, exported category
    /// ids, and the settings payload. Owned by DvmConsole.Core so the WPF
    /// console and headless tooling share one definition.
    /// </summary>
    public class SettingsTransferFile
    {
        /// <summary>
        /// Format identifier for dvmconsole settings transfer files.
        /// </summary>
        public const string FORMAT = "dvmconsole-settings-transfer";

        public string Format { get; set; } = FORMAT;
        public int Version { get; set; } = 1;
        public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
        public List<string> Categories { get; set; } = new List<string>();
        public JObject Settings { get; set; } = new JObject();
    }
}
