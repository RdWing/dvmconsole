// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the pure managed hotkey-capture slice:
* DvmConsole.Avalonia.Input.KeyGestureMapper and
* DvmConsole.Avalonia.ViewModels.HotkeyCaptureViewModel.
*
* KeyGestureMapper contract: a public static class with exactly one
* public static method
*   bool TryMap(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers
*               modifiers, out DvmConsole.Platform.Hotkeys.HotkeyGesture
*               gesture)
* It returns false (with the zero gesture) for Key.None, the
* modifier-only keys (LeftCtrl/RightCtrl/LeftShift/RightShift/LeftAlt/
* RightAlt/LWin/RWin), OemPlus, and NumPad0; true for A-Z, D0-D9,
* F1-F24, Enter/Escape/Tab/Space/Backspace/Delete/Insert/Home/End/
* PageUp/PageDown/Left/Right/Up/Down. Avalonia names the backspace
* key Key.Back, so Key.Back maps to HotkeyKey.Backspace — the only
* name mismatch between the two enums. Avalonia modifiers Alt/Control/
* Shift/Meta map to the same-named HotkeyModifiers flags, None maps
* None, and every supported combination maps. The mapper is pure:
* no mutation, native, network, or persistence behavior.
*
* HotkeyCaptureViewModel contract: a public sealed
* INotifyPropertyChanged view-model with exactly one constructor
* (PttCapabilityViewModel?), an exact get-only bool IsCapturing, exact
* methods StartCapture(), Cancel(), ApplyKey(HotkeyGesture gesture),
* ClearHotkey(), and the PropertyChanged event. With a null Ptt every
* method is a no-op and IsCapturing stays false. With a Ptt:
* StartCapture raises IsCapturing true change-only (repeat silent),
* Cancel raises false change-only, ApplyKey acts only while capturing
* and only for a non-None gesture — calling Ptt.SetHotkey exactly once,
* exiting capture with one IsCapturing-false notification (idle and
* None are no-ops), and ClearHotkey calls Ptt.ClearHotkey and cancels
* capture change-only. The slice never registers, unregisters, or
* disposes anything on the hotkey service and is not IDisposable.
*
* The tests are fully headless and pure managed: no Avalonia.Headless
* package, window, display, native call, file, or secret is involved.
*
* RED contract gate: neither production type exists yet, so this file
* is expected to fail compilation until they land.
*/
#nullable enable
using System.ComponentModel;
using System.Reflection;
using Avalonia.Input;
using DvmConsole.Avalonia.Input;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Executable RED contract gate for <c>KeyGestureMapper</c> and
    /// <c>HotkeyCaptureViewModel</c>.
    /// </summary>
    public sealed class HotkeyCaptureViewModelTests
    {
        // ---- KeyGestureMapper: supported-key matrix ------------------------------

        /// <summary>
        /// The full supported key matrix, listed explicitly against the
        /// contract: A-Z, D0-D9, F1-F24, and the fifteen named
        /// navigation/editing keys. Avalonia names the backspace key
        /// <see cref="Key.Back"/>, so that key maps to
        /// <see cref="HotkeyKey.Backspace"/> — the one name mismatch
        /// between the two enums. (The ranges are contiguous in both
        /// enums, verified against the referenced assemblies.)
        /// </summary>
        public static IEnumerable<object[]> SupportedKeys()
        {
            for (var i = 0; i < 26; i++)
            {
                yield return new object[]
                {
                    (Key)((int)Key.A + i),
                    (HotkeyKey)((int)HotkeyKey.A + i),
                };
            }

            for (var i = 0; i < 10; i++)
            {
                yield return new object[]
                {
                    (Key)((int)Key.D0 + i),
                    (HotkeyKey)((int)HotkeyKey.D0 + i),
                };
            }

            for (var i = 0; i < 24; i++)
            {
                yield return new object[]
                {
                    (Key)((int)Key.F1 + i),
                    (HotkeyKey)((int)HotkeyKey.F1 + i),
                };
            }

            yield return new object[] { Key.Enter, HotkeyKey.Enter };
            yield return new object[] { Key.Escape, HotkeyKey.Escape };
            yield return new object[] { Key.Tab, HotkeyKey.Tab };
            yield return new object[] { Key.Space, HotkeyKey.Space };
            yield return new object[] { Key.Back, HotkeyKey.Backspace };
            yield return new object[] { Key.Delete, HotkeyKey.Delete };
            yield return new object[] { Key.Insert, HotkeyKey.Insert };
            yield return new object[] { Key.Home, HotkeyKey.Home };
            yield return new object[] { Key.End, HotkeyKey.End };
            yield return new object[] { Key.PageUp, HotkeyKey.PageUp };
            yield return new object[] { Key.PageDown, HotkeyKey.PageDown };
            yield return new object[] { Key.Left, HotkeyKey.Left };
            yield return new object[] { Key.Right, HotkeyKey.Right };
            yield return new object[] { Key.Up, HotkeyKey.Up };
            yield return new object[] { Key.Down, HotkeyKey.Down };
        }

        /// <summary>
        /// Locks the supported matrix to the exact contract set: 75 keys
        /// (26 letters, 10 digits, 24 function keys, 15 named keys), with
        /// the rejected boundary keys excluded.
        /// </summary>
        [Fact]
        public void SupportedKeyMatrix_ExactContractSet()
        {
            var keys = SupportedKeys().Select(row => (Key)row[0]!).ToArray();

            Assert.Equal(75, keys.Length);
            Assert.Contains(Key.A, keys);
            Assert.Contains(Key.Z, keys);
            Assert.Contains(Key.D0, keys);
            Assert.Contains(Key.D9, keys);
            Assert.Contains(Key.F1, keys);
            Assert.Contains(Key.F24, keys);
            Assert.Contains(Key.Enter, keys);
            Assert.Contains(Key.Escape, keys);
            Assert.Contains(Key.Tab, keys);
            Assert.Contains(Key.Space, keys);
            Assert.Contains(Key.Back, keys);
            Assert.Contains(Key.Delete, keys);
            Assert.Contains(Key.Insert, keys);
            Assert.Contains(Key.Home, keys);
            Assert.Contains(Key.End, keys);
            Assert.Contains(Key.PageUp, keys);
            Assert.Contains(Key.PageDown, keys);
            Assert.Contains(Key.Left, keys);
            Assert.Contains(Key.Right, keys);
            Assert.Contains(Key.Up, keys);
            Assert.Contains(Key.Down, keys);

            Assert.DoesNotContain(Key.None, keys);
            Assert.DoesNotContain(Key.LeftCtrl, keys);
            Assert.DoesNotContain(Key.RightCtrl, keys);
            Assert.DoesNotContain(Key.LeftShift, keys);
            Assert.DoesNotContain(Key.RightShift, keys);
            Assert.DoesNotContain(Key.LeftAlt, keys);
            Assert.DoesNotContain(Key.RightAlt, keys);
            Assert.DoesNotContain(Key.LWin, keys);
            Assert.DoesNotContain(Key.RWin, keys);
            Assert.DoesNotContain(Key.OemPlus, keys);
            Assert.DoesNotContain(Key.NumPad0, keys);
        }

        /// <summary>
        /// Every supported key maps true with no modifiers to the
        /// same-named <see cref="HotkeyKey"/> and
        /// <see cref="HotkeyModifiers.None"/>.
        /// </summary>
        [Theory]
        [MemberData(nameof(SupportedKeys))]
        public void TryMap_SupportedKey_NoModifiers_MapsToSameKeyNoneModifiers(Key key, HotkeyKey expected)
        {
            var mapped = KeyGestureMapper.TryMap(key, KeyModifiers.None, out var gesture);

            Assert.True(mapped);
            Assert.Equal(expected, gesture.Key);
            Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        }

        // ---- KeyGestureMapper: rejected keys --------------------------------------

        /// <summary>
        /// The rejected keys: none, the modifier-only keys, the plus
        /// operator key, and the keypad zero.
        /// </summary>
        public static IEnumerable<object[]> RejectedKeys()
        {
            yield return new object[] { Key.None };
            yield return new object[] { Key.LeftCtrl };
            yield return new object[] { Key.RightCtrl };
            yield return new object[] { Key.LeftShift };
            yield return new object[] { Key.RightShift };
            yield return new object[] { Key.LeftAlt };
            yield return new object[] { Key.RightAlt };
            yield return new object[] { Key.LWin };
            yield return new object[] { Key.RWin };
            yield return new object[] { Key.OemPlus };
            yield return new object[] { Key.NumPad0 };
        }

        /// <summary>
        /// Every rejected key returns false with the zero gesture
        /// (None/None) and no modifiers involved.
        /// </summary>
        [Theory]
        [MemberData(nameof(RejectedKeys))]
        public void TryMap_RejectedKey_ReturnsFalseAndZeroGesture(Key key)
        {
            var mapped = KeyGestureMapper.TryMap(key, KeyModifiers.None, out var gesture);

            Assert.False(mapped);
            Assert.Equal(HotkeyKey.None, gesture.Key);
            Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        }

        /// <summary>
        /// A rejected key stays rejected regardless of the modifiers
        /// supplied, and the out gesture stays zero.
        /// </summary>
        [Theory]
        [MemberData(nameof(RejectedKeys))]
        public void TryMap_RejectedKey_WithModifiers_StillFalse(Key key)
        {
            var mapped = KeyGestureMapper.TryMap(
                key,
                KeyModifiers.Control | KeyModifiers.Alt,
                out var gesture);

            Assert.False(mapped);
            Assert.Equal(HotkeyKey.None, gesture.Key);
            Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        }

        // ---- KeyGestureMapper: modifiers -------------------------------------------

        /// <summary>
        /// Each single Avalonia modifier maps to the same-named
        /// HotkeyModifiers flag while the key is preserved.
        /// </summary>
        [Fact]
        public void TryMap_SingleModifiers_MapToExactFlags()
        {
            var pairs = new (KeyModifiers Avalonia, HotkeyModifiers Platform)[]
            {
                (KeyModifiers.Alt, HotkeyModifiers.Alt),
                (KeyModifiers.Control, HotkeyModifiers.Control),
                (KeyModifiers.Shift, HotkeyModifiers.Shift),
                (KeyModifiers.Meta, HotkeyModifiers.Meta),
            };

            foreach (var (avalonia, platform) in pairs)
            {
                var mapped = KeyGestureMapper.TryMap(Key.A, avalonia, out var gesture);

                Assert.True(mapped);
                Assert.Equal(HotkeyKey.A, gesture.Key);
                Assert.Equal(platform, gesture.Modifiers);
            }
        }

        /// <summary>
        /// Every supported modifier combination maps: the Avalonia and
        /// platform enums share the same flag bits (Alt=1, Control=2,
        /// Shift=4, Meta=8), so all sixteen combinations translate
        /// one-to-one while the key is preserved.
        /// </summary>
        [Fact]
        public void TryMap_AllModifierCombinations_MapToSameFlagBits()
        {
            for (var i = 0; i < 16; i++)
            {
                var mapped = KeyGestureMapper.TryMap(Key.F5, (KeyModifiers)i, out var gesture);

                Assert.True(mapped);
                Assert.Equal(HotkeyKey.F5, gesture.Key);
                Assert.Equal((HotkeyModifiers)i, gesture.Modifiers);
            }
        }

        // ---- KeyGestureMapper: compile-time shape ------------------------------------

        /// <summary>
        /// Locks the exact public surface of <c>KeyGestureMapper</c>: a
        /// static class in the DvmConsole.Avalonia.Input namespace with
        /// exactly one public static method, <c>TryMap</c>, taking
        /// (Key, KeyModifiers, out HotkeyGesture) and returning bool.
        /// </summary>
        [Fact]
        public void KeyGestureMapperShape_ExactPublicSurface()
        {
            var type = typeof(KeyGestureMapper);

            Assert.True(type.IsClass);
            Assert.True(type.IsAbstract);
            Assert.True(type.IsSealed);
            Assert.Equal("DvmConsole.Avalonia.Input", type.Namespace);
            Assert.Same(typeof(MainWindowViewModel).Assembly, type.Assembly);

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .ToArray();

            var method = Assert.Single(methods);
            Assert.Equal("TryMap", method.Name);
            Assert.Equal(typeof(bool), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(
                new[] { typeof(Key), typeof(KeyModifiers), typeof(HotkeyGesture).MakeByRefType() },
                parameters.Select(p => p.ParameterType).ToArray());
            Assert.True(parameters[2].IsOut);
        }

        // ---- HotkeyCaptureViewModel: fixtures -----------------------------------------

        /// <summary>
        /// Mutable, headless <see cref="IGlobalHotkeyService"/> fake with
        /// every member counted so tests can prove the capture slice never
        /// registers, unregisters, or disposes anything and only queries
        /// capability through Ptt's own SetHotkey.
        /// </summary>
        private sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
        {
            private readonly Dictionary<HotkeyGesture, HotkeyCapability> capabilities = new();

            /// <summary>Total <see cref="GetCapability"/> calls.</summary>
            public int GetCapabilityCalls { get; private set; }

            /// <summary>Total <see cref="RegisterAsync"/> calls.</summary>
            public int RegisterCalls { get; private set; }

            /// <summary>Total <see cref="UnregisterAsync"/> calls.</summary>
            public int UnregisterCalls { get; private set; }

            /// <summary>Total <see cref="Dispose"/> calls.</summary>
            public int DisposeCalls { get; private set; }

            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

            /// <summary>Configures the capability reported for a gesture.</summary>
            public void SetCapability(HotkeyGesture gesture, HotkeyCapability capability)
                => capabilities[gesture] = capability;

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
            {
                GetCapabilityCalls++;
                return capabilities.TryGetValue(gesture, out var capability)
                    ? capability
                    : HotkeyCapability.Unsupported;
            }

            public Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
            {
                RegisterCalls++;
                return Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture));
            }

            public Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
            {
                UnregisterCalls++;
                return Task.CompletedTask;
            }

            public void Dispose() => DisposeCalls++;
        }

        /// <summary>Builds a fresh fake, real Ptt slice, and capture slice.</summary>
        private static (FakeGlobalHotkeyService Hotkeys, PttCapabilityViewModel Ptt, HotkeyCaptureViewModel Capture) Create()
        {
            var hotkeys = new FakeGlobalHotkeyService();
            var ptt = new PttCapabilityViewModel(
                hotkeys,
                () => null,
                () => new ChannelSlotViewModel[0]);
            var capture = new HotkeyCaptureViewModel(ptt);
            return (hotkeys, ptt, capture);
        }

        /// <summary>Records every <see cref="INotifyPropertyChanged.PropertyChanged"/> name.</summary>
        private static List<string?> Track(INotifyPropertyChanged source)
        {
            var changes = new List<string?>();
            source.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            return changes;
        }

        /// <summary>Records every <see cref="PttCapabilityViewModel.HotkeyChangeRequested"/> payload.</summary>
        private static List<HotkeyGesture?> TrackHotkeyRequests(PttCapabilityViewModel ptt)
        {
            var requests = new List<HotkeyGesture?>();
            ptt.HotkeyChangeRequested += gesture => requests.Add(gesture);
            return requests;
        }

        // ---- HotkeyCaptureViewModel: compile-time shape --------------------------------

        /// <summary>
        /// Locks the exact public surface of <c>HotkeyCaptureViewModel</c>:
        /// sealed, notifiable, non-disposable, in the Avalonia view-model
        /// namespace, with exactly the contract ctor
        /// (PttCapabilityViewModel?), the exact get-only IsCapturing
        /// property, the exact four methods, and the PropertyChanged
        /// event. Compiler-generated backing members are allowed; anything
        /// else declared public fails this gate.
        /// </summary>
        [Fact]
        public void ApiShape_ExactPublicSurface()
        {
            var type = typeof(HotkeyCaptureViewModel);

            Assert.True(type.IsClass);
            Assert.True(type.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(type));
            Assert.False(typeof(IDisposable).IsAssignableFrom(type));
            Assert.Equal("DvmConsole.Avalonia.ViewModels", type.Namespace);
            Assert.Same(typeof(MainWindowViewModel).Assembly, type.Assembly);

            // Exactly one public instance ctor with the exact contract signature.
            var ctor = Assert.Single(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.Equal(
                new[] { typeof(PttCapabilityViewModel) },
                ctor.GetParameters().Select(p => p.ParameterType).ToArray());

            // Exactly the one declared public instance property, get-only bool.
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name)
                .ToArray();
            Assert.Equal(new[] { "IsCapturing" }, properties.Select(p => p.Name).ToArray());
            Assert.Equal(typeof(bool), properties[0].PropertyType);
            Assert.False(properties[0].CanWrite);

            // Exactly the four declared public instance methods (accessors excluded).
            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name)
                .ToArray();
            Assert.Equal(
                new[] { "ApplyKey", "Cancel", "ClearHotkey", "StartCapture" },
                methods.Select(m => m.Name).ToArray());
            Assert.Equal(typeof(void), type.GetMethod("StartCapture", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("Cancel", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("ClearHotkey", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("ApplyKey", new[] { typeof(HotkeyGesture) })!.ReturnType);

            // Exactly the one declared public instance event.
            var events = type
                .GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(e => e.Name)
                .ToArray();
            Assert.Equal(new[] { "PropertyChanged" }, events.Select(e => e.Name).ToArray());
            Assert.Equal(typeof(PropertyChangedEventHandler), events[0].EventHandlerType);
        }

        // ---- HotkeyCaptureViewModel: null Ptt -------------------------------------------

        /// <summary>
        /// With a null Ptt every method is a no-op: IsCapturing stays
        /// false and no PropertyChanged is ever raised.
        /// </summary>
        [Fact]
        public void NullPtt_AllMethodsNoOp_IsCapturingStaysFalse()
        {
            var capture = new HotkeyCaptureViewModel(null);
            var changes = Track(capture);

            capture.StartCapture();
            Assert.False(capture.IsCapturing);

            capture.Cancel();
            capture.ApplyKey(new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control));
            capture.ClearHotkey();

            Assert.False(capture.IsCapturing);
            Assert.Empty(changes);
        }

        // ---- HotkeyCaptureViewModel: capture lifecycle ----------------------------------

        /// <summary>
        /// StartCapture flips IsCapturing true with exactly one
        /// notification; a repeated StartCapture while already capturing
        /// is silent.
        /// </summary>
        [Fact]
        public void StartCapture_ChangeOnly_RepeatIsSilent()
        {
            var (_, _, capture) = Create();
            var changes = Track(capture);

            capture.StartCapture();

            Assert.True(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);

            capture.StartCapture();

            Assert.True(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);
        }

        /// <summary>
        /// Cancel flips IsCapturing false with exactly one notification;
        /// a repeated Cancel while already idle is silent.
        /// </summary>
        [Fact]
        public void Cancel_ChangeOnly_RepeatIsSilent()
        {
            var (_, _, capture) = Create();
            capture.StartCapture();
            var changes = Track(capture);

            capture.Cancel();

            Assert.False(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);

            capture.Cancel();

            Assert.False(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);
        }

        /// <summary>
        /// Applying a valid gesture while capturing calls Ptt.SetHotkey
        /// exactly once (Ptt.Hotkey set, HotkeyChangeRequested raised once
        /// with the gesture, one capability query), exits capture, and
        /// raises exactly one IsCapturing-false notification.
        /// </summary>
        [Fact]
        public void ApplyKey_WhileCapturing_CallsSetHotkeyOnce_ExitsCapture_NotifiesOnce()
        {
            var (hotkeys, ptt, capture) = Create();
            capture.StartCapture();
            var changes = Track(capture);
            var requests = TrackHotkeyRequests(ptt);
            var gesture = new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

            capture.ApplyKey(gesture);

            Assert.Equal(gesture, ptt.Hotkey);
            Assert.Equal(new List<HotkeyGesture?> { gesture }, requests);
            Assert.Equal(1, hotkeys.GetCapabilityCalls);
            Assert.False(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);
        }

        /// <summary>
        /// Applying a gesture while idle is a no-op: no Ptt call, no
        /// notification, capture stays false.
        /// </summary>
        [Fact]
        public void ApplyKey_WhileIdle_NoOp()
        {
            var (hotkeys, ptt, capture) = Create();
            var changes = Track(capture);
            var requests = TrackHotkeyRequests(ptt);

            capture.ApplyKey(new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control));

            Assert.Null(ptt.Hotkey);
            Assert.Empty(requests);
            Assert.Equal(0, hotkeys.GetCapabilityCalls);
            Assert.False(capture.IsCapturing);
            Assert.Empty(changes);
        }

        /// <summary>
        /// Applying a None-key gesture while capturing is a no-op: no Ptt
        /// call, no notification, and capture stays active.
        /// </summary>
        [Fact]
        public void ApplyKey_NoneKey_WhileCapturing_NoOp_StaysCapturing()
        {
            var (hotkeys, ptt, capture) = Create();
            capture.StartCapture();
            var changes = Track(capture);
            var requests = TrackHotkeyRequests(ptt);

            capture.ApplyKey(new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.None));

            Assert.Null(ptt.Hotkey);
            Assert.Empty(requests);
            Assert.Equal(0, hotkeys.GetCapabilityCalls);
            Assert.True(capture.IsCapturing);
            Assert.Empty(changes);
        }

        /// <summary>
        /// Capture can be re-entered after an apply: each successful apply
        /// forwards exactly one SetHotkey request to Ptt.
        /// </summary>
        [Fact]
        public void ApplyKey_CanCaptureAgain_AfterFirstApply()
        {
            var (_, ptt, capture) = Create();
            var requests = TrackHotkeyRequests(ptt);
            var first = new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control);
            var second = new HotkeyGesture(HotkeyKey.F2, HotkeyModifiers.Alt);

            capture.StartCapture();
            capture.ApplyKey(first);
            capture.StartCapture();
            capture.ApplyKey(second);

            Assert.Equal(new List<HotkeyGesture?> { first, second }, requests);
            Assert.Equal(second, ptt.Hotkey);
            Assert.False(capture.IsCapturing);
        }

        // ---- HotkeyCaptureViewModel: clear hotkey ----------------------------------------

        /// <summary>
        /// ClearHotkey while capturing cancels capture with exactly one
        /// change-only notification; with no configured hotkey Ptt raises
        /// nothing.
        /// </summary>
        [Fact]
        public void ClearHotkey_WhileCapturing_CancelsCapture_ChangeOnly()
        {
            var (_, ptt, capture) = Create();
            capture.StartCapture();
            var changes = Track(capture);
            var requests = TrackHotkeyRequests(ptt);

            capture.ClearHotkey();

            Assert.False(capture.IsCapturing);
            Assert.Equal(new List<string?> { "IsCapturing" }, changes);
            Assert.Empty(requests);
        }

        /// <summary>
        /// ClearHotkey always calls Ptt.ClearHotkey — even while idle —
        /// forwarding the null clear request to Ptt, and adds no capture
        /// notification when capture was already idle.
        /// </summary>
        [Fact]
        public void ClearHotkey_CallsPttClearHotkey_EvenWhileIdle()
        {
            var (_, ptt, capture) = Create();
            var set = new HotkeyGesture(HotkeyKey.F5, HotkeyModifiers.Alt);
            ptt.SetHotkey(set);
            var requests = TrackHotkeyRequests(ptt);
            var changes = Track(capture);

            capture.ClearHotkey();

            Assert.Null(ptt.Hotkey);
            Assert.Equal(new List<HotkeyGesture?> { null }, requests);
            Assert.False(capture.IsCapturing);
            Assert.Empty(changes);
        }

        // ---- HotkeyCaptureViewModel: service isolation -------------------------------------

        /// <summary>
        /// A full capture lifecycle performs no service registration,
        /// unregistration, or disposal; the only service interaction is
        /// Ptt's own single capability query for the applied gesture.
        /// </summary>
        [Fact]
        public void CaptureLifecycle_NoServiceRegistrationOrDisposal()
        {
            var (hotkeys, ptt, capture) = Create();

            capture.StartCapture();
            capture.ApplyKey(new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control));
            capture.ClearHotkey();
            capture.StartCapture();
            capture.Cancel();

            Assert.Equal(1, hotkeys.GetCapabilityCalls);
            Assert.Equal(0, hotkeys.RegisterCalls);
            Assert.Equal(0, hotkeys.UnregisterCalls);
            Assert.Equal(0, hotkeys.DisposeCalls);
            Assert.Null(ptt.Hotkey);
        }
    }
}
