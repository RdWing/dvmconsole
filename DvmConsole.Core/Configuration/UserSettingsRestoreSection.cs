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

#nullable enable
using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned restore-selection state. Resource identities remain strings
    /// so the section is portable and can be hydrated without a live platform
    /// or connection object.
    /// </summary>
    public sealed class UserSettingsRestoreSection
    {
        /// <summary>Stable resource keys selected at the previous shutdown.</summary>
        public List<string> SelectedChannels { get; set; } = new();

        /// <summary>
        /// Avalonia's persisted primary resource extension; WPF has no separate
        /// primary key, but retaining it avoids losing the dashboard role.
        /// </summary>
        public string? PrimaryResourceKey { get; set; }

        /// <summary>Selectable-encryption state keyed by stable resource identity.</summary>
        public Dictionary<string, bool> SelectableEncryptionStates { get; set; } =
            new(System.StringComparer.OrdinalIgnoreCase);
    }
}
