// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the macOS global-hotkey adapter slice
* (plan Task 8: implement macOS global-hotkey and permission behavior):
*
*   DvmConsole.Platform.Hotkeys.Mac.MacGlobalHotkeyService
*   DvmConsole.Platform.Hotkeys.Mac.MacKeyStateReader
*   DvmConsole.Platform.Hotkeys.Mac.MacHotkeyKeyCodes
*   DvmConsole.Platform.Hotkeys.Mac.IMacEventTap (+ MacKeyEventData)
*   DvmConsole.Platform.Hotkeys.Mac.IHotkeyPermissionProbe
*     (+ HotkeyPermissionStatus)
*
* The service is a CGEventTap-backed IGlobalHotkeyService with a
* permission model: GetCapability reports PermissionRequired when the
* injected probe denies (result cached after first probe),
* Available when granted and the gesture is mappable, Unsupported for
* the None key, unmappable keys, and off-macOS. RegisterAsync returns
* PermissionDenied (preserving the gesture) when permission is
* missing — it never prompts and never bypasses TCC; the event tap is
* never created without permission. One shared tap serves all
* registered gestures (WPF single-hook parity); the first successful
* register creates/enables/attaches the tap, the last unregister
* detaches/disables it. Raw tap events are matched by keycode plus
* exact supported-modifier state (extra non-modifier bits ignored),
* autorepeat events suppressed, unregistered gestures silent.
* Pre-cancelled RegisterAsync throws OperationCanceledException
* (documented; unlike the fallback service). Dispose is idempotent,
* detaches HotkeyPressed, stops the tap, and must not deadlock when a
* tap callback fires reentrantly during teardown.
*
* MacKeyStateReader is the GetAsyncKeyState-contract replacement for
* the PTT key-up watchdog: an injected keycode probe delegate
* (CGEventSourceKeyState-backed in production; the parameterless ctor
* returns false off-macOS and never throws).
*
* MacHotkeyKeyCodes is the pure kVK mapper: HotkeyKey <-> keycode and
* HotkeyModifiers <-> CGEventFlags, with a supported-modifier mask so
* the service can match exact modifier state while ignoring
* non-modifier flag bits (caps lock etc.).
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Hotkeys.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for the macOS global-hotkey adapter slice.
    /// </summary>
    public sealed class MacGlobalHotkeyTests
    {
        /* ------------------------------------------------------------------
        ** Test doubles
        ** ---------------------------------------------------------------- */

        private sealed class FakeEventTap : IMacEventTap
        {
            public bool CreateResult = true;
            public int CreateCount;
            public int EnableCount;
            public int DisableCount;
            public int AttachCount;
            public int DetachCount;
            public int DisposeCount;

            /// <summary>
            /// Models the real tap's attach latch: AttachRunLoop only
            /// counts while the tap is attached, and DetachRunLoop clears
            /// the latch (parity with CoreGraphicsEventTap's _attached
            /// flag, where a re-attach before teardown completes no-ops).
            /// </summary>
            public bool Attached;
            public bool RaiseDuringDetach;

            /// <summary>
            /// When set, DetachRunLoop blocks until the gate is released
            /// (models a teardown in flight on another thread).
            /// </summary>
            public System.Threading.ManualResetEventSlim? DetachGate;

            public event Action<MacKeyEventData>? KeyEvent;

            public bool Create()
            {
                CreateCount++;
                return CreateResult;
            }

            public void Enable() => EnableCount++;

            public void Disable() => DisableCount++;

            public void AttachRunLoop()
            {
                if (Attached)
                {
                    return; // stale re-attach before teardown completes: no-op
                }

                Attached = true;
                AttachCount++;
            }

            public void DetachRunLoop()
            {
                DetachCount++;
                DetachGate?.Wait();
                Attached = false;
                if (RaiseDuringDetach)
                {
                    // Reentrant callback during teardown: the service must
                    // not deadlock or throw.
                    KeyEvent?.Invoke(new MacKeyEventData(0x00, 0, false));
                }
            }

            public void SimulateKeyEvent(MacKeyEventData data) => KeyEvent?.Invoke(data);

            public void Dispose() => DisposeCount++;
        }

        private sealed class FakePermissionProbe : IHotkeyPermissionProbe
        {
            public HotkeyPermissionStatus Result;
            public int QueryCount;

            public FakePermissionProbe(HotkeyPermissionStatus result = HotkeyPermissionStatus.Granted)
            {
                Result = result;
            }

            public HotkeyPermissionStatus Query()
            {
                QueryCount++;
                return Result;
            }
        }

        private static MacGlobalHotkeyService CreateService(
            FakeEventTap tap,
            FakePermissionProbe probe,
            bool isMacOS = true)
            => new MacGlobalHotkeyService(tap, probe, () => isMacOS);

        /* ------------------------------------------------------------------
        ** Capability
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task NonMacOs_EverythingUnsupported_SeamNeverTouched()
        {
            var tap = new FakeEventTap();
            var probe = new FakePermissionProbe();
            var service = CreateService(tap, probe, isMacOS: false);

            Assert.Equal(
                HotkeyCapability.Unsupported,
                service.GetCapability(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta)));
            var register = await service.RegisterAsync(
                new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta),
                CancellationToken.None);
            Assert.Equal(HotkeyRegistrationStatus.Unsupported, register.Status);
            Assert.Equal(0, probe.QueryCount);
            Assert.Equal(0, tap.CreateCount);
        }

        [Fact]
        public void PermissionDenied_CapabilityPermissionRequired_ProbedOnceCached()
        {
            var tap = new FakeEventTap();
            var probe = new FakePermissionProbe(HotkeyPermissionStatus.AccessibilityRequired);
            var service = CreateService(tap, probe);

            var first = service.GetCapability(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None));
            var second = service.GetCapability(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None));

            Assert.Equal(HotkeyCapability.PermissionRequired, first);
            Assert.Equal(HotkeyCapability.PermissionRequired, second);
            Assert.Equal(1, probe.QueryCount); // cached after first probe
        }

        [Fact]
        public void GrantedMappable_Available_NoneKeyUnsupported()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));

            Assert.Equal(
                HotkeyCapability.Available,
                service.GetCapability(new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control | HotkeyModifiers.Alt)));
            Assert.Equal(
                HotkeyCapability.Unsupported,
                service.GetCapability(new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.None)));
        }

        /* ------------------------------------------------------------------
        ** Registration
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task RegisterWithoutPermission_PermissionDenied_GesturePreserved_TapNeverCreated()
        {
            var tap = new FakeEventTap();
            var probe = new FakePermissionProbe(HotkeyPermissionStatus.InputMonitoringRequired);
            var service = CreateService(tap, probe);
            var gesture = new HotkeyGesture(HotkeyKey.B, HotkeyModifiers.Shift);

            var result = await service.RegisterAsync(gesture, CancellationToken.None);

            Assert.Equal(HotkeyRegistrationStatus.PermissionDenied, result.Status);
            Assert.Equal(gesture, result.Gesture);
            Assert.Equal(0, tap.CreateCount);
        }

        [Fact]
        public async Task RegisterGranted_TapCreatedOnceAcrossGestures()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));

            var first = await service.RegisterAsync(
                new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta), CancellationToken.None);
            var second = await service.RegisterAsync(
                new HotkeyGesture(HotkeyKey.B, HotkeyModifiers.Control), CancellationToken.None);

            Assert.Equal(HotkeyRegistrationStatus.Registered, first.Status);
            Assert.Equal(HotkeyRegistrationStatus.Registered, second.Status);
            Assert.Equal(1, tap.CreateCount);
            Assert.Equal(1, tap.EnableCount);
            Assert.Equal(1, tap.AttachCount);
        }

        [Fact]
        public async Task RegisterDuplicate_AlreadyRegistered()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);

            await service.RegisterAsync(gesture, CancellationToken.None);
            var duplicate = await service.RegisterAsync(gesture, CancellationToken.None);

            Assert.Equal(HotkeyRegistrationStatus.AlreadyRegistered, duplicate.Status);
            Assert.Equal(gesture, duplicate.Gesture);
            Assert.Equal(1, tap.CreateCount);
        }

        [Fact]
        public async Task RegisterPreCancelled_ThrowsOperationCanceled()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.RegisterAsync(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta), cts.Token));
        }

        [Fact]
        public async Task TapCreateFailure_RegisterUnsupported()
        {
            var tap = new FakeEventTap { CreateResult = false };
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));

            var result = await service.RegisterAsync(
                new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta), CancellationToken.None);

            Assert.Equal(HotkeyRegistrationStatus.Unsupported, result.Status);
        }

        /* ------------------------------------------------------------------
        ** Unregister / teardown
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task UnregisterPreCancelled_ThrowsOperationCanceled()
        {
            // Parity with RegisterAsync's documented OCE deviation: a
            // pre-cancelled unregister must not silently no-op.
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.UnregisterAsync(gesture, cts.Token));
        }

        [Fact]
        public async Task RegisterAsync_AfterDispose_ThrowsObjectDisposed()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            service.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                service.RegisterAsync(gesture, CancellationToken.None));
        }

        [Fact]
        public async Task UnregisterAsync_AfterDispose_ThrowsObjectDisposed()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            service.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                service.UnregisterAsync(gesture, CancellationToken.None));
        }

        [Fact]
        public async Task ConcurrentUnregisterAndRegister_TeardownNegotiated_NoSilentDeath()
        {
            // The teardown (DetachRunLoop) runs outside the registration
            // lock so a reentrant callback cannot deadlock; a concurrent
            // register must therefore negotiate with the in-flight
            // teardown instead of racing it. Here the fake blocks inside
            // DetachRunLoop while the register arrives: the register must
            // wait for the teardown to finish, then install a fresh tap —
            // never returning Registered against a tap that is about to be
            // torn down (silent hotkey death).
            var tap = new FakeEventTap { DetachGate = new System.Threading.ManualResetEventSlim() };
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            var unregister = Task.Run(() => service.UnregisterAsync(gesture, CancellationToken.None));
            // Wait until the teardown is inside DetachRunLoop (blocked).
            while (tap.DetachCount == 0)
            {
                await Task.Delay(1);
            }

            var register = Task.Run(() => service.RegisterAsync(gesture, CancellationToken.None));
            await Task.Delay(50); // give the register a chance to race

            tap.DetachGate.Set(); // release the teardown
            await unregister;
            var result = await register;

            Assert.Equal(HotkeyRegistrationStatus.Registered, result.Status);
            Assert.Equal(2, tap.CreateCount);   // fresh tap after teardown
            Assert.Equal(2, tap.AttachCount);

            // The fresh tap actually delivers events.
            var events = new List<HotkeyEventArgs>();
            service.HotkeyPressed += (_, e) => events.Add(e);
            MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.A, out var keyCode);
            tap.SimulateKeyEvent(new MacKeyEventData(
                keyCode, MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.Meta), false));
            var single = Assert.Single(events);
            Assert.Equal(HotkeyEventType.Pressed, single.EventType);
        }

        [Fact]
        public async Task Unregister_Idempotent_LastUnregisterTearsDownTap()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            await service.UnregisterAsync(gesture, CancellationToken.None);
            await service.UnregisterAsync(gesture, CancellationToken.None); // idempotent

            Assert.Equal(1, tap.DetachCount);
            Assert.Equal(1, tap.DisableCount);

            // Re-registration after unregister succeeds with a fresh tap.
            var again = await service.RegisterAsync(gesture, CancellationToken.None);
            Assert.Equal(HotkeyRegistrationStatus.Registered, again.Status);
            Assert.Equal(2, tap.CreateCount);
        }

        /* ------------------------------------------------------------------
        ** Event dispatch
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task SyntheticKeyEvents_RaisePressedAndReleased_WithMappedGesture()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            var events = new List<HotkeyEventArgs>();
            service.HotkeyPressed += (_, e) => events.Add(e);

            MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.A, out var keyCode);
            var flags = MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.Meta);
            tap.SimulateKeyEvent(new MacKeyEventData(keyCode, flags, IsAutorepeat: false));
            tap.SimulateKeyEvent(new MacKeyEventData(keyCode, flags, IsAutorepeat: false));

            Assert.Equal(2, events.Count);
            Assert.Equal(HotkeyEventType.Pressed, events[0].EventType);
            Assert.Equal(HotkeyEventType.Released, events[1].EventType);
            Assert.All(events, e => Assert.Equal(gesture, e.Gesture));
        }

        [Fact]
        public async Task AutorepeatEvents_Suppressed()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            var gesture = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta);
            await service.RegisterAsync(gesture, CancellationToken.None);

            var events = new List<HotkeyEventArgs>();
            service.HotkeyPressed += (_, e) => events.Add(e);

            MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.A, out var keyCode);
            var flags = MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.Meta);
            tap.SimulateKeyEvent(new MacKeyEventData(keyCode, flags, IsAutorepeat: true));

            Assert.Empty(events);
        }

        [Fact]
        public async Task UnregisteredGesture_AndWrongModifiers_Silent()
        {
            var tap = new FakeEventTap();
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            await service.RegisterAsync(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta), CancellationToken.None);

            var events = new List<HotkeyEventArgs>();
            service.HotkeyPressed += (_, e) => events.Add(e);

            MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.B, out var otherKey);
            MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.A, out var keyA);
            var metaFlags = MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.Meta);

            tap.SimulateKeyEvent(new MacKeyEventData(otherKey, metaFlags, false));      // wrong key
            tap.SimulateKeyEvent(new MacKeyEventData(keyA, 0, false));                  // wrong modifiers
            tap.SimulateKeyEvent(new MacKeyEventData(keyA, metaFlags, false));          // match -> released

            var single = Assert.Single(events);
            Assert.Equal(HotkeyEventType.Released, single.EventType);
        }

        [Fact]
        public async Task DisposeTwice_DetachesEvent_StopsTap_NoDeadlockOnReentrantCallback()
        {
            var tap = new FakeEventTap { RaiseDuringDetach = true };
            var service = CreateService(tap, new FakePermissionProbe(HotkeyPermissionStatus.Granted));
            await service.RegisterAsync(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta), CancellationToken.None);
            var events = new List<HotkeyEventArgs>();
            service.HotkeyPressed += (_, e) => events.Add(e);

            service.Dispose();
            service.Dispose(); // idempotent

            Assert.Equal(1, tap.DetachCount);
            Assert.Equal(1, tap.DisposeCount);
            Assert.Empty(events); // detached: the reentrant callback did not reach subscribers
        }

        /* ------------------------------------------------------------------
        ** Mapper
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Mapper_RoundTripsAllSupportedKeys()
        {
            var supported = new[]
            {
                // A-Z (26)
                HotkeyKey.A, HotkeyKey.Z, HotkeyKey.M, HotkeyKey.Q,
                // D0-D9 (10)
                HotkeyKey.D0, HotkeyKey.D5, HotkeyKey.D9,
                // F1-F24 (24)
                HotkeyKey.F1, HotkeyKey.F12, HotkeyKey.F24,
                // named (15)
                HotkeyKey.Enter, HotkeyKey.Escape, HotkeyKey.Tab, HotkeyKey.Space,
                HotkeyKey.Backspace, HotkeyKey.Delete, HotkeyKey.Insert, HotkeyKey.Home,
                HotkeyKey.End, HotkeyKey.PageUp, HotkeyKey.PageDown, HotkeyKey.Left,
                HotkeyKey.Right, HotkeyKey.Up, HotkeyKey.Down,
            };

            foreach (var key in supported)
            {
                Assert.True(MacHotkeyKeyCodes.TryGetKeyCode(key, out var keyCode), key.ToString());
                Assert.True(MacHotkeyKeyCodes.TryGetHotkeyKey(keyCode, out var roundTrip), key.ToString());
                Assert.Equal(key, roundTrip);
            }

            Assert.False(MacHotkeyKeyCodes.TryGetKeyCode(HotkeyKey.None, out _));
        }

        [Fact]
        public void Mapper_ModifierRoundTrip_AllSixteenCombinations()
        {
            for (var i = 0; i < 16; i++)
            {
                var modifiers = (HotkeyModifiers)i;
                var flags = MacHotkeyKeyCodes.GetEventFlags(modifiers);
                var roundTrip = MacHotkeyKeyCodes.ToModifiers(flags);
                Assert.Equal(modifiers, roundTrip);
            }

            Assert.Equal(0ul, MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.None));
        }

        [Fact]
        public void Mapper_ToModifiers_IgnoresNonModifierFlagBits()
        {
            // Caps-lock (kCGEventFlagMaskAlphaShift = 1 << 16) and other
            // non-modifier bits must not pollute the modifier state.
            var capsLock = 1ul << 16;
            var metaFlags = MacHotkeyKeyCodes.GetEventFlags(HotkeyModifiers.Meta);

            Assert.Equal(HotkeyModifiers.Meta, MacHotkeyKeyCodes.ToModifiers(metaFlags | capsLock));
            Assert.Equal(HotkeyModifiers.None, MacHotkeyKeyCodes.ToModifiers(capsLock));
        }

        [Fact]
        public void Mapper_PublicStaticSurface_IsExact()
        {
            var type = typeof(MacHotkeyKeyCodes);
            Assert.True(type.IsAbstract && type.IsSealed);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(
                new[] { "GetEventFlags", "ToModifiers", "TryGetHotkeyKey", "TryGetKeyCode" },
                methods);
            Assert.NotNull(type.GetField("SupportedModifierMask", BindingFlags.Public | BindingFlags.Static));
        }

        /* ------------------------------------------------------------------
        ** Key state reader
        ** ---------------------------------------------------------------- */

        [Fact]
        public void MacKeyStateReader_ForwardsToProbeDelegate()
        {
            var probed = new List<ushort>();
            var reader = new MacKeyStateReader(keyCode =>
            {
                probed.Add(keyCode);
                return keyCode == 0x00; // 'A' on ANSI kVK
            });

            Assert.True(reader.IsKeyDown(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None)));
            Assert.False(reader.IsKeyDown(new HotkeyGesture(HotkeyKey.B, HotkeyModifiers.None)));
            Assert.Equal(2, probed.Count);
        }

        [Fact]
        public void MacKeyStateReader_NullProbe_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MacKeyStateReader(null!));
        }

        [Fact]
        public void MacKeyStateReader_Parameterless_OffMacOs_ReturnsFalse_NeverThrows()
        {
            // On this Linux host OperatingSystem.IsMacOS() is false, so the
            // parameterless reader must answer false for every gesture.
            var reader = new MacKeyStateReader();
            Assert.False(reader.IsKeyDown(new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.Meta)));
            Assert.False(reader.IsKeyDown(new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.None)));
        }

        /* ------------------------------------------------------------------
        ** Surface shapes
        ** ---------------------------------------------------------------- */

        [Fact]
        public void HotkeyPermissionStatus_HasExactMembers()
        {
            Assert.Equal(
                new[] { "NotApplicable", "Granted", "AccessibilityRequired", "InputMonitoringRequired" },
                Enum.GetNames(typeof(HotkeyPermissionStatus)));
        }

        [Fact]
        public void MacKeyEventData_IsRecordWithKeyCodeFlagsAutorepeat()
        {
            var type = typeof(MacKeyEventData);
            Assert.True(type.IsValueType);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(ushort), typeof(ulong), typeof(bool),
            }));
            Assert.NotNull(type.GetProperty("KeyCode"));
            Assert.NotNull(type.GetProperty("Flags"));
            Assert.NotNull(type.GetProperty("IsAutorepeat"));
        }
    }
}
