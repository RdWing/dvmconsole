// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// Small, explicit P/Invoke surface for the Apple CoreGraphics event-tap
    /// APIs and the ApplicationServices accessibility APIs used by the macOS
    /// global-hotkey adapters. Mirrors the conventions of
    /// <c>DvmConsole.Platform.Audio.Mac.CoreAudioNative</c>: explicit framework
    /// paths, Cdecl calling convention, and guarded managed helpers so no
    /// native symbol is ever resolved off macOS.
    /// </summary>
    internal static class CoreGraphicsNative
    {
        internal const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        internal const string ApplicationServicesLibrary = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        internal const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        /*
        ** CGEventTap configuration constants.
        */

        /// <summary>kCGHIDEventTap: tap the system HID event stream.</summary>
        internal const uint KcHidEventTap = 0;

        /// <summary>kCGHeadInsertEventTap: insert the tap at the head of the stream.</summary>
        internal const uint KcHeadInsertEventTap = 0;

        /// <summary>kCGEventTapOptionListenOnly: observe events without modifying them.</summary>
        internal const uint KcEventTapOptionListenOnly = 0;

        /// <summary>kCGEventKeyDown.</summary>
        internal const uint EventKeyDown = 10;

        /// <summary>kCGEventKeyUp.</summary>
        internal const uint EventKeyUp = 11;

        /// <summary>kCGEventMaskKeyDown.</summary>
        internal const ulong EventMaskKeyDown = 1ul << 10;

        /// <summary>kCGEventMaskKeyUp.</summary>
        internal const ulong EventMaskKeyUp = 1ul << 11;

        /*
        ** CGEventField constants.
        */

        /// <summary>kCGKeyboardEventKeycode.</summary>
        internal const uint KeyboardEventKeycode = 9;

        /// <summary>kCGKeyboardEventAutorepeat.</summary>
        internal const uint KeyboardEventAutorepeat = 8;

        /// <summary>kCGEventSourceStateCombinedSessionState.</summary>
        internal const uint EventSourceStateCombinedSession = 0;

        /// <summary>kCFStringEncodingUTF8.</summary>
        private const uint CfStringEncodingUtf8 = 0x08000100;

        /*
        ** Delegates.
        */

        /// <summary>
        /// kCGEventTapCallBack: invoked by the event tap for every event in
        /// the tap's mask. Returns the (possibly modified) event; a
        /// listen-only tap returns the event unchanged.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr CGEventTapCallBack(
            IntPtr proxy,
            uint type,
            IntPtr eventRef,
            IntPtr userInfo);

        /*
        ** CoreGraphics event-tap API.
        */

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr CGEventTapCreate(
            uint tap,
            uint place,
            uint options,
            ulong eventsOfInterest,
            CGEventTapCallBack callback,
            IntPtr userInfo);

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CGEventTapEnable(
            IntPtr tap,
            [MarshalAs(UnmanagedType.I1)] bool enable);

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long CGEventGetIntegerValueField(IntPtr eventRef, uint field);

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong CGEventGetFlags(IntPtr eventRef);

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CGEventSourceKeyState(uint stateId, ushort keyCode);

        [DllImport(CoreGraphicsLibrary, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CGPreflightListenEventAccess();

        /*
        ** ApplicationServices accessibility API.
        */

        [DllImport(ApplicationServicesLibrary, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

        /*
        ** CoreFoundation run-loop API.
        */

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr CFMachPortCreateRunLoopSource(
            IntPtr allocator,
            IntPtr port,
            nint order);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CFMachPortInvalidate(IntPtr port);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CFRelease(IntPtr cfObject);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator,
            IntPtr cString,
            uint encoding);

        /*
        ** Guarded managed helpers.
        */

        /// <summary>
        /// Creates the kCFRunLoopCommonModes mode string. Callers own the
        /// returned reference and must release it via <see cref="CFRelease"/>.
        /// </summary>
        internal static IntPtr CreateCommonRunLoopMode()
        {
            var bytes = Encoding.UTF8.GetBytes("kCFRunLoopCommonModes\0");
            var native = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, native, bytes.Length);
                return CFStringCreateWithCString(IntPtr.Zero, native, CfStringEncodingUtf8);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        /// <summary>
        /// Physical key-state probe backing <see cref="MacKeyStateReader"/>:
        /// CGEventSourceKeyState for the combined session state. Off macOS
        /// this answers false without ever resolving a native symbol.
        /// </summary>
        internal static bool KeyStateIsDown(ushort keyCode)
            => OperatingSystem.IsMacOS() && CGEventSourceKeyState(EventSourceStateCombinedSession, keyCode);
    }
}
