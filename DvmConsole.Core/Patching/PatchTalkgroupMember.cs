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

namespace dvmconsole
{
    /// <summary>
    /// A single talkgroup membership inside a patch group. Raw caller-supplied
    /// values are normalized (trimmed, case-insensitive system identity) by
    /// <see cref="PatchManager.ApplyMemberships"/>; the normalized spelling is
    /// what <see cref="PatchManager"/> forwards with.
    /// </summary>
    public sealed class PatchTalkgroupMember
    {
        /// <summary>Name of the radio system the talkgroup belongs to.</summary>
        public string SystemName { get; set; }

        /// <summary>Talkgroup id on that system.</summary>
        public string Tgid { get; set; }
    }
}
