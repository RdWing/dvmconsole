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

using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned groups and patches settings section, persisted by
    /// <see cref="SettingsSectionStore"/>. Property names and nested JSON
    /// shapes mirror the WPF SettingsManager group transfer properties.
    /// </summary>
    /// <remarks>
    /// The DTO preserves member order and raw identity values. PatchManager is
    /// the runtime boundary that trims and de-duplicates identities; this
    /// section does not normalize, validate, or apply group behavior.
    /// <para>
    /// Patch enabled-state retention is controlled by the existing
    /// <see cref="UserSettingsPreferencesSection.RetainPatchStateOnStartup"/>
    /// preference and is intentionally not duplicated here.
    /// </para>
    /// </remarks>
    public sealed class UserSettingsGroupSection
    {
        /// <summary>
        /// Saved patch and multi-select memberships scoped by codeplug context
        /// key, then group name. Member list order is significant for one-way
        /// patching because member one is the source.
        /// </summary>
        public Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>> PatchGroupMemberships { get; set; } = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>();

        /// <summary>
        /// Saved one-way mode scoped by codeplug context key and group name.
        /// </summary>
        public Dictionary<string, Dictionary<string, bool>> PatchGroupModes { get; set; } = new Dictionary<string, Dictionary<string, bool>>();

        /// <summary>
        /// Saved enabled state scoped by codeplug context key and group name.
        /// </summary>
        public Dictionary<string, Dictionary<string, bool>> PatchGroupEnabledStates { get; set; } = new Dictionary<string, Dictionary<string, bool>>();
    }
}
