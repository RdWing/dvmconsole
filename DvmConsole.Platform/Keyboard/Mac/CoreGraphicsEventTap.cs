// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// Concrete <see cref="IMacEventTap"/> over a listen-only
    /// CGEventTap. <see cref="Create"/> installs the tap on the HID event
    /// stream (kCGHIDEventTap, kCGHeadInsertEventTap,
    /// kCGEventTapOptionListenOnly) and builds its run-loop source;
    /// <see cref="Enable"/>/<see cref="Disable"/> toggle CGEventTapEnable;
    /// <see cref="AttachRunLoop"/>/<see cref="DetachRunLoop"/> add/remove the
    /// source on the caller's run loop (common modes) and detach also
    /// disables the tap. The callback reads the keycode and autorepeat
    /// fields plus the event flags and raises <see cref="KeyEvent"/>;
    /// <see cref="SimulateKeyEvent"/> raises it directly as a test seam.
    /// Safe to construct on any host; native code is only ever invoked on
    /// macOS.
    /// </summary>
    public sealed class CoreGraphicsEventTap : IMacEventTap
    {
        private readonly CoreGraphicsNative.CGEventTapCallBack _callback;

        private IntPtr _machPort;
        private IntPtr _runLoopSource;
        private IntPtr _runLoopMode;
        private bool _attached;

        /// <summary>
        /// Safe on any host: no native code runs in the constructor.
        /// </summary>
        public CoreGraphicsEventTap()
        {
            // Held on a field for the tap's lifetime so the native event tap,
            // which retains the callback proc, can never dangle.
            _callback = OnEvent;
        }

        /// <summary>Raised for every raw keyboard event the tap observes.</summary>
        public event Action<MacKeyEventData>? KeyEvent;

        /// <summary>
        /// Creates the CGEventTap and its run-loop source. Returns false off
        /// macOS or when CoreGraphics cannot install the tap; the instance
        /// stays inert until a later successful Create.
        /// </summary>
        public bool Create()
        {
            if (_machPort != IntPtr.Zero)
            {
                return true;
            }

            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            _machPort = CoreGraphicsNative.CGEventTapCreate(
                CoreGraphicsNative.KcHidEventTap,
                CoreGraphicsNative.KcHeadInsertEventTap,
                CoreGraphicsNative.KcEventTapOptionListenOnly,
                CoreGraphicsNative.EventMaskKeyDown | CoreGraphicsNative.EventMaskKeyUp,
                _callback,
                IntPtr.Zero);
            if (_machPort == IntPtr.Zero)
            {
                return false;
            }

            _runLoopSource = CoreGraphicsNative.CFMachPortCreateRunLoopSource(
                IntPtr.Zero,
                _machPort,
                0);
            if (_runLoopSource == IntPtr.Zero)
            {
                CoreGraphicsNative.CFMachPortInvalidate(_machPort);
                CoreGraphicsNative.CFRelease(_machPort);
                _machPort = IntPtr.Zero;
                return false;
            }

            return true;
        }

        /// <summary>Starts event delivery from the created tap.</summary>
        public void Enable()
        {
            if (!OperatingSystem.IsMacOS() || _machPort == IntPtr.Zero)
            {
                return;
            }

            CoreGraphicsNative.CGEventTapEnable(_machPort, true);
        }

        /// <summary>Stops event delivery from the created tap.</summary>
        public void Disable()
        {
            if (!OperatingSystem.IsMacOS() || _machPort == IntPtr.Zero)
            {
                return;
            }

            CoreGraphicsNative.CGEventTapEnable(_machPort, false);
        }

        /// <summary>
        /// Attaches the tap source to the caller's run loop in the common
        /// modes, so events flow during tracking loops as well as the default
        /// mode. No-op when the tap was not created or is already attached.
        /// </summary>
        public void AttachRunLoop()
        {
            if (!OperatingSystem.IsMacOS() || _runLoopSource == IntPtr.Zero || _attached)
            {
                return;
            }

            if (_runLoopMode == IntPtr.Zero)
            {
                _runLoopMode = CoreGraphicsNative.CreateCommonRunLoopMode();
            }

            if (_runLoopMode == IntPtr.Zero)
            {
                return;
            }

            CoreGraphicsNative.CFRunLoopAddSource(
                CoreGraphicsNative.CFRunLoopGetCurrent(),
                _runLoopSource,
                _runLoopMode);
            _attached = true;
        }

        /// <summary>
        /// Detaches the tap source from the caller's run loop and disables
        /// the tap. No-op when the tap was not created or not attached.
        /// </summary>
        public void DetachRunLoop()
        {
            if (!OperatingSystem.IsMacOS() || _runLoopSource == IntPtr.Zero || !_attached)
            {
                return;
            }

            if (_runLoopMode != IntPtr.Zero)
            {
                CoreGraphicsNative.CFRunLoopRemoveSource(
                    CoreGraphicsNative.CFRunLoopGetCurrent(),
                    _runLoopSource,
                    _runLoopMode);
            }

            _attached = false;

            if (_machPort != IntPtr.Zero)
            {
                CoreGraphicsNative.CGEventTapEnable(_machPort, false);
            }
        }

        /// <summary>
        /// Raises <see cref="KeyEvent"/> directly with the supplied data,
        /// bypassing the OS event stream. Test seam; managed-only.
        /// </summary>
        public void SimulateKeyEvent(MacKeyEventData data) => KeyEvent?.Invoke(data);

        /// <summary>
        /// Detaches, disables and invalidates the tap and releases the
        /// run-loop source and mode strings. Idempotent; no-op off macOS.
        /// </summary>
        public void Dispose()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            DetachRunLoop();

            if (_machPort != IntPtr.Zero)
            {
                CoreGraphicsNative.CFMachPortInvalidate(_machPort);
                CoreGraphicsNative.CFRelease(_machPort);
                _machPort = IntPtr.Zero;
            }

            if (_runLoopSource != IntPtr.Zero)
            {
                CoreGraphicsNative.CFRelease(_runLoopSource);
                _runLoopSource = IntPtr.Zero;
            }

            if (_runLoopMode != IntPtr.Zero)
            {
                CoreGraphicsNative.CFRelease(_runLoopMode);
                _runLoopMode = IntPtr.Zero;
            }
        }

        private IntPtr OnEvent(IntPtr proxy, uint type, IntPtr eventRef, IntPtr userInfo)
        {
            if (eventRef == IntPtr.Zero)
            {
                return eventRef;
            }

            var keyCode = unchecked((ushort)CoreGraphicsNative.CGEventGetIntegerValueField(
                eventRef,
                CoreGraphicsNative.KeyboardEventKeycode));
            var isAutorepeat = CoreGraphicsNative.CGEventGetIntegerValueField(
                eventRef,
                CoreGraphicsNative.KeyboardEventAutorepeat) != 0;
            var flags = CoreGraphicsNative.CGEventGetFlags(eventRef);

            KeyEvent?.Invoke(new MacKeyEventData(keyCode, flags, isAutorepeat));

            // Listen-only tap: pass the event through untouched.
            return eventRef;
        }
    }
}
