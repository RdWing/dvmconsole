// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
*/

using System;
using System.Collections.Generic;

namespace dvmconsole
{
    /// <summary>
    /// Core-owned TAR Viewer column-visibility settings section. The
    /// merge-preserving settings store persists this named section alongside
    /// the existing TAR/audio/PTT sections; unknown keys are tolerated so newer
    /// viewers can be opened by older builds without rejecting the file.
    /// </summary>
    public sealed class UserSettingsTarViewerSection
    {
        /// <summary>
        /// Column visibility keyed by the stable TAR Viewer descriptor key.
        /// Missing keys use the viewer's WPF-parity defaults.
        /// </summary>
        public Dictionary<string, bool> ColumnVisibility { get; set; } =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }
}
