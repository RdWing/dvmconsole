// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// Concrete <see cref="IHotkeyPermissionProbe"/> over the macOS TCC
    /// checks: AXIsProcessTrustedWithOptions (with no prompt option, so this
    /// never pops a dialog) for Accessibility, and CGPreflightListenEventAccess
    /// for Input Monitoring. Off macOS the verdict is
    /// <see cref="HotkeyPermissionStatus.NotApplicable"/>. Safe to construct
    /// and query on any host; native code is only ever invoked on macOS.
    /// </summary>
    public sealed class MacPermissionProbe : IHotkeyPermissionProbe
    {
        /// <summary>
        /// Queries the current permission state. Accessibility is checked
        /// first (a listen-only event tap cannot be installed without it);
        /// then Input Monitoring (required to observe keyboard events on
        /// macOS 10.15 and later); only then Granted.
        /// </summary>
        public HotkeyPermissionStatus Query()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return HotkeyPermissionStatus.NotApplicable;
            }

            if (!CoreGraphicsNative.AXIsProcessTrustedWithOptions(IntPtr.Zero))
            {
                return HotkeyPermissionStatus.AccessibilityRequired;
            }

            if (!CoreGraphicsNative.CGPreflightListenEventAccess())
            {
                return HotkeyPermissionStatus.InputMonitoringRequired;
            }

            return HotkeyPermissionStatus.Granted;
        }
    }
}
