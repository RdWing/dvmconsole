// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// The macOS TCC permission state relevant to global-hotkey capture.
    /// </summary>
    public enum HotkeyPermissionStatus
    {
        /// <summary>The permission model does not apply on this host.</summary>
        NotApplicable,

        /// <summary>All required permissions are granted.</summary>
        Granted,

        /// <summary>Accessibility permission (System Settings &gt; Privacy &amp;
        /// Security &gt; Accessibility) is required but not granted.</summary>
        AccessibilityRequired,

        /// <summary>Input Monitoring permission (System Settings &gt; Privacy
        /// &amp; Security &gt; Input Monitoring) is required but not granted.</summary>
        InputMonitoringRequired,
    }

    /// <summary>
    /// Seam over the macOS TCC permission checks for global-hotkey capture.
    /// Implementations must be safe to query on any host and never invoke
    /// native code off macOS.
    /// </summary>
    public interface IHotkeyPermissionProbe
    {
        /// <summary>Queries the current permission state.</summary>
        HotkeyPermissionStatus Query();
    }
}
