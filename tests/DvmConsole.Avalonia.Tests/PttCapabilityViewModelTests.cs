// SPDX-License-Identifier: AGPL-3.0-only
/**
* Contract gate for the managed PTT capability state slice:
*
*   DvmConsole.Avalonia.ViewModels.PttCapabilityViewModel
*   DvmConsole.Avalonia.ViewModels.ChannelSlotViewModel.PttEngaged
*
* PttCapabilityViewModel is a sealed INotifyPropertyChanged view-model
* constructed from an IGlobalHotkeyService plus two callbacks that resolve
* the PTT target set at press time: a primary-channel resolver
* (Func<ChannelSlotViewModel?>) and a selected-channels resolver
* (Func<IReadOnlyCollection<ChannelSlotViewModel>>). The primary resolver
* wins when it returns a slot; otherwise AllChannels=true resolves the
* target snapshot from the selected-channels resolver; otherwise there is
* no target and engagement is a no-op.
*
* The view-model is pure managed state: it never calls
* RegisterAsync/UnregisterAsync/Dispose on the service (capability is the
* only service query, and only for the currently configured gesture), has
* no IDisposable surface, and exposes its hotkey events to the shell via
* public ApplyHotkeyPress(HotkeyGesture, HotkeyEventType). Hotkey is a
* get-only HotkeyGesture? (null until SetHotkey), Capability is a get-only
* HotkeyCapability (HotkeyCapability.Unsupported while no gesture is
* configured, otherwise the service's GetCapability result for the current
* gesture), ToggleMode and AllChannels are settable, and IsEngaged is
* get-only. SetHotkey rejects HotkeyKey.None with ArgumentException,
* otherwise assigns the gesture, re-queries capability, raises
* Hotkey/Capability PropertyChanged once each (change-only) and raises
* HotkeyChangeRequested exactly once and raises SaveRequested only for an
* effective persisted change. ClearHotkey is a no-op while Hotkey
* is already null, otherwise resets Hotkey to null and Capability to
* Unsupported (change-only notifications) and raises
* HotkeyChangeRequested(null).
*
* Engagement is a single shared state driven by either the pointer
* (PttPointerDown/PttPointerUp) or the hotkey path. In momentary mode
* (ToggleMode=false) a down engages the press-time target snapshot once
* (each target PttEngaged=true, IsEngaged=true, PttStateRequested(true))
* and is idempotent while engaged; an up releases exactly the press-time
* snapshot (PttEngaged=false, IsEngaged=false, PttStateRequested(false))
* and is idempotent while released. In toggle mode (ToggleMode=true) a
* down toggles engagement/release and an up is a no-op. Hotkey events act
* only when a gesture is configured and the incoming gesture equals it:
* momentary Pressed=down and Released=up; toggle Pressed=down and
* Released=no-op. Mismatched or unconfigured gestures are no-ops. Duplicate
* target references engage/release once and request once.
*
* All notifications are change-only: ToggleMode, AllChannels, IsEngaged,
* Hotkey, Capability and ChannelSlotViewModel.PttEngaged (a new public
* get/set bool, default false, read-only notification on change only;
* the slot's existing ctor/properties are unchanged).
*
* The tests are fully headless and pure managed: a private in-test fake
* implements IGlobalHotkeyService with configurable capability, manually
* raised events, completed registration and no-op disposal; no
* Avalonia.Headless package, window, display, native call, file, or
* secret is involved. This file is the executable RED contract for the
* managed slice — PttCapabilityViewModel and PttEngaged do not exist yet.
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Executable RED contract gate for <c>PttCapabilityViewModel</c> and
    /// <c>ChannelSlotViewModel.PttEngaged</c>.
    /// </summary>
    public sealed class PttCapabilityViewModelTests
    {
        // ---- Fixtures ---------------------------------------------------------

        /// <summary>
        /// Mutable, headless <see cref="IGlobalHotkeyService"/> fake:
        /// per-gesture capability configured explicitly (defaulting to
        /// <see cref="HotkeyCapability.Unsupported"/>), events raised
        /// manually, registration/unregistration completed, disposal a
        /// no-op. Every member is counted so tests can prove the slice
        /// never registers, unregisters, disposes, or queries beyond the
        /// configured gesture.
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

            /// <summary>Manually raises <see cref="HotkeyPressed"/>.</summary>
            public void RaisePressed(HotkeyGesture gesture, HotkeyEventType eventType)
                => HotkeyPressed?.Invoke(this, new HotkeyEventArgs(gesture, eventType));

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

        /// <summary>Builds a fresh fake plus view-model with no targets by default.</summary>
        private static (FakeGlobalHotkeyService Hotkeys, PttCapabilityViewModel Vm) Create(
            Func<ChannelSlotViewModel?>? primary = null,
            Func<IReadOnlyCollection<ChannelSlotViewModel>>? selected = null)
        {
            var hotkeys = new FakeGlobalHotkeyService();
            var vm = new PttCapabilityViewModel(
                hotkeys,
                primary ?? (() => null),
                selected ?? (() => new ChannelSlotViewModel[0]));
            return (hotkeys, vm);
        }

        /// <summary>Builds a channel slot with the given 1-based number.</summary>
        private static ChannelSlotViewModel Slot(int number)
            => new(number, $"CHANNEL {number:00}");

        /// <summary>Records every <see cref="INotifyPropertyChanged.PropertyChanged"/> name.</summary>
        private static List<string> Track(INotifyPropertyChanged source)
        {
            var changes = new List<string>();
            source.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? string.Empty);
            return changes;
        }

        /// <summary>Records every <see cref="PttCapabilityViewModel.HotkeyChangeRequested"/> payload.</summary>
        private static List<HotkeyGesture?> TrackHotkeyRequests(PttCapabilityViewModel vm)
        {
            var requests = new List<HotkeyGesture?>();
            vm.HotkeyChangeRequested += gesture => requests.Add(gesture);
            return requests;
        }

        /// <summary>Records every <see cref="PttCapabilityViewModel.PttStateRequested"/> payload.</summary>
        private static List<bool> TrackPttRequests(PttCapabilityViewModel vm)
        {
            var requests = new List<bool>();
            vm.PttStateRequested += engaged => requests.Add(engaged);
            return requests;
        }

        // ---- Compile-time shape -------------------------------------------------

        /// <summary>
        /// Locks the exact public surface of <c>PttCapabilityViewModel</c>:
        /// sealed, notifiable, non-disposable, in the Avalonia view-model
        /// with exactly the contract ctor, six declared public
        /// properties with exact types/accessibility, five declared public
        /// methods with exact signatures, and four declared public events
        /// with exact handler types. Compiler-generated backing members are
        /// allowed; anything else declared public fails this gate.
        /// </summary>
        [Fact]
        public void ApiShape_ExactPublicSurface()
        {
            var type = typeof(PttCapabilityViewModel);

            Assert.True(type.IsClass);
            Assert.True(type.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(type));
            Assert.False(typeof(IDisposable).IsAssignableFrom(type));
            Assert.Equal("DvmConsole.Avalonia.ViewModels", type.Namespace);
            Assert.Same(typeof(ChannelSlotViewModel).Assembly, type.Assembly);

            // Exactly one public instance ctor with the exact contract signature.
            var ctor = Assert.Single(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.Equal(
                new[]
                {
                    typeof(IGlobalHotkeyService),
                    typeof(Func<ChannelSlotViewModel?>),
                    typeof(Func<IReadOnlyCollection<ChannelSlotViewModel>>),
                },
                ctor.GetParameters().Select(p => p.ParameterType).ToArray());

            // Exactly the six declared public instance properties.
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name)
                .ToArray();
            Assert.Equal(
                new[] { "AllChannels", "Capability", "EngagedTargets", "Hotkey", "IsEngaged", "ToggleMode" },
                properties.Select(p => p.Name).ToArray());

            Assert.Equal(typeof(HotkeyGesture?), type.GetProperty("Hotkey")!.PropertyType);
            Assert.Equal(typeof(HotkeyCapability), type.GetProperty("Capability")!.PropertyType);
            Assert.Equal(typeof(bool), type.GetProperty("ToggleMode")!.PropertyType);
            Assert.Equal(typeof(bool), type.GetProperty("AllChannels")!.PropertyType);
            Assert.Equal(typeof(bool), type.GetProperty("IsEngaged")!.PropertyType);
            Assert.Equal(typeof(IReadOnlyList<ChannelSlotViewModel>), type.GetProperty("EngagedTargets")!.PropertyType);

            Assert.False(type.GetProperty("Hotkey")!.CanWrite);
            Assert.False(type.GetProperty("Capability")!.CanWrite);
            Assert.True(type.GetProperty("ToggleMode")!.CanWrite);
            Assert.True(type.GetProperty("AllChannels")!.CanWrite);
            Assert.False(type.GetProperty("IsEngaged")!.CanWrite);
            Assert.False(type.GetProperty("EngagedTargets")!.CanWrite);

            // Exactly the five declared public instance methods (accessors excluded).
            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name)
                .ToArray();
            Assert.Equal(
                new[] { "ApplyHotkeyPress", "ClearHotkey", "PttPointerDown", "PttPointerUp", "SetHotkey" },
                methods.Select(m => m.Name).ToArray());

            Assert.Equal(typeof(void), type.GetMethod("PttPointerDown", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("PttPointerUp", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("ClearHotkey", Type.EmptyTypes)!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("ApplyHotkeyPress", new[] { typeof(HotkeyGesture), typeof(HotkeyEventType) })!.ReturnType);
            Assert.Equal(typeof(void), type.GetMethod("SetHotkey", new[] { typeof(HotkeyGesture) })!.ReturnType);

            // Exactly the four declared public instance events.
            var events = type
                .GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(e => e.Name)
                .ToArray();
            Assert.Equal(
                new[] { "HotkeyChangeRequested", "PropertyChanged", "PttStateRequested", "SaveRequested" },
                events.Select(e => e.Name).ToArray());

            Assert.Equal(typeof(PropertyChangedEventHandler), type.GetEvent("PropertyChanged")!.EventHandlerType);
            Assert.Equal(typeof(Action<bool>), type.GetEvent("PttStateRequested")!.EventHandlerType);
            Assert.Equal(typeof(Action<HotkeyGesture?>), type.GetEvent("HotkeyChangeRequested")!.EventHandlerType);
            Assert.Equal(typeof(Action<HotkeyGesture?, bool, bool>), type.GetEvent("SaveRequested")!.EventHandlerType);
        }

        /// <summary>
        /// Locks the new <c>ChannelSlotViewModel.PttEngaged</c> member:
        /// declared on the slot itself, public get/set bool, default false.
        /// The slot's existing ctor and properties are not asserted here —
        /// they are already locked by MainWindowViewModelTests.
        /// </summary>
        [Fact]
        public void ApiShape_ChannelSlotPttEngaged()
        {
            var property = typeof(ChannelSlotViewModel).GetProperty("PttEngaged", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property!.PropertyType);
            Assert.True(property.CanRead);
            Assert.True(property.CanWrite);
            Assert.Equal(typeof(ChannelSlotViewModel), property.DeclaringType);
        }

        // ---- Constructor --------------------------------------------------------

        [Fact]
        public void Ctor_NullHotkeys_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PttCapabilityViewModel(
                null!, () => null, () => new ChannelSlotViewModel[0]));
        }

        [Fact]
        public void Ctor_NullPrimaryChannel_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PttCapabilityViewModel(
                new FakeGlobalHotkeyService(), null!, () => new ChannelSlotViewModel[0]));
        }

        [Fact]
        public void Ctor_NullSelectedChannels_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new PttCapabilityViewModel(
                new FakeGlobalHotkeyService(), () => null, null!));
        }

        [Fact]
        public void Ctor_InitialState_DefaultsAndNoServiceAccess()
        {
            var (hotkeys, vm) = Create();

            Assert.Null(vm.Hotkey);
            Assert.Equal(HotkeyCapability.Unsupported, vm.Capability);
            Assert.False(vm.ToggleMode);
            Assert.False(vm.AllChannels);
            Assert.False(vm.IsEngaged);

            // Capability is queried only for a configured gesture.
            Assert.Equal(0, hotkeys.GetCapabilityCalls);
            Assert.Equal(0, hotkeys.RegisterCalls);
            Assert.Equal(0, hotkeys.UnregisterCalls);
        }

        // ---- Hotkey configuration and capability ----------------------------------

        [Fact]
        public void SetHotkey_NoneKey_ThrowsArgumentException_AndStateUnchanged()
        {
            var (hotkeys, vm) = Create();
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            Assert.Throws<ArgumentException>(() => vm.SetHotkey(new HotkeyGesture(HotkeyKey.None, HotkeyModifiers.None)));

            Assert.Null(vm.Hotkey);
            Assert.Equal(HotkeyCapability.Unsupported, vm.Capability);
            Assert.Empty(changes);
            Assert.Empty(requests);
            Assert.Equal(0, hotkeys.GetCapabilityCalls);
        }

        [Fact]
        public void SetHotkey_ValidGesture_AssignsQueriesAndRaisesOnceEach()
        {
            var (hotkeys, vm) = Create();
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            vm.SetHotkey(gesture);

            Assert.Equal(gesture, vm.Hotkey);
            Assert.Equal(HotkeyCapability.Available, vm.Capability);
            Assert.Single(changes.Where(n => n == "Hotkey"));
            Assert.Single(changes.Where(n => n == "Capability"));
            Assert.Equal(gesture, Assert.Single(requests));
            Assert.Equal(1, hotkeys.GetCapabilityCalls);
            Assert.Equal(0, hotkeys.RegisterCalls);
        }

        [Fact]
        public void SetHotkey_ChangeGesture_QueriesNewCapabilityAndNotifies()
        {
            var (hotkeys, vm) = Create();
            var first = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            var second = new HotkeyGesture(HotkeyKey.F10, HotkeyModifiers.Alt);
            hotkeys.SetCapability(first, HotkeyCapability.Available);
            hotkeys.SetCapability(second, HotkeyCapability.PermissionRequired);
            vm.SetHotkey(first);
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            vm.SetHotkey(second);

            Assert.Equal(second, vm.Hotkey);
            Assert.Equal(HotkeyCapability.PermissionRequired, vm.Capability);
            Assert.Single(changes.Where(n => n == "Hotkey"));
            Assert.Single(changes.Where(n => n == "Capability"));
            Assert.Equal(second, Assert.Single(requests));
            Assert.Equal(2, hotkeys.GetCapabilityCalls);
        }

        [Fact]
        public void SetHotkey_SameGesture_ReRequestsOnce_NoRedundantPropertyChanges()
        {
            var (hotkeys, vm) = Create();
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            vm.SetHotkey(gesture);

            // Values unchanged: change-only notifications stay silent.
            Assert.Empty(changes);
            // The change request is raised exactly once per successful set.
            Assert.Equal(gesture, Assert.Single(requests));
            Assert.Equal(2, hotkeys.GetCapabilityCalls);
        }

        [Fact]
        public void SetHotkey_SameGesture_ReQueriesCapability_NotifiesOnlyOnCapabilityChange()
        {
            var (hotkeys, vm) = Create();
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            hotkeys.SetCapability(gesture, HotkeyCapability.PermissionRequired);
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            vm.SetHotkey(gesture);

            Assert.Empty(changes.Where(n => n == "Hotkey"));
            Assert.Single(changes.Where(n => n == "Capability"));
            Assert.Equal(HotkeyCapability.PermissionRequired, vm.Capability);
            Assert.Equal(gesture, Assert.Single(requests));
        }

        [Fact]
        public void ClearHotkey_WhenNull_NoOp()
        {
            var (hotkeys, vm) = Create();
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);

            vm.ClearHotkey();

            Assert.Null(vm.Hotkey);
            Assert.Equal(HotkeyCapability.Unsupported, vm.Capability);
            Assert.Empty(changes);
            Assert.Empty(requests);
            Assert.Equal(0, hotkeys.GetCapabilityCalls);
        }

        [Fact]
        public void ClearHotkey_WhenSet_ResetsHotkeyAndCapability_RequestsNull()
        {
            var (hotkeys, vm) = Create();
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            var changes = Track(vm);
            var requests = TrackHotkeyRequests(vm);
            var queriesBeforeClear = hotkeys.GetCapabilityCalls;

            vm.ClearHotkey();

            Assert.Null(vm.Hotkey);
            Assert.Equal(HotkeyCapability.Unsupported, vm.Capability);
            Assert.Single(changes.Where(n => n == "Hotkey"));
            Assert.Single(changes.Where(n => n == "Capability"));
            Assert.Null(Assert.Single(requests));
            // Clearing never queries the service.
            Assert.Equal(queriesBeforeClear, hotkeys.GetCapabilityCalls);
        }

        // ---- Target resolution ------------------------------------------------------

        [Fact]
        public void PointerDown_PrimaryWinsOverAllChannels()
        {
            var primary = Slot(1);
            var other = Slot(2);
            var (_, vm) = Create(primary: () => primary, selected: () => new[] { other });
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.True(primary.PttEngaged);
            Assert.False(other.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Equal(new[] { true }, requests.ToArray());
        }

        [Fact]
        public void PointerDown_AllChannels_UsesSelectionSnapshot()
        {
            var first = Slot(1);
            var second = Slot(2);
            var (_, vm) = Create(primary: () => null, selected: () => new[] { first, second });
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.True(first.PttEngaged);
            Assert.True(second.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Equal(new[] { true }, requests.ToArray());
        }

        [Fact]
        public void PointerDown_AllChannels_SkipsGlobalPttDisabledCards()
        {
            var enabled = Slot(1);
            var disabled = Slot(2);
            disabled.IsGlobalPttEnabled = false;
            var (_, vm) = Create(primary: () => null, selected: () => new[] { enabled, disabled });
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.True(enabled.PttEngaged);
            Assert.False(disabled.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Equal(new[] { true }, requests.ToArray());
        }

        [Fact]
        public void PointerDown_AllChannels_AllDisabled_NoTarget_NoOp()
        {
            var disabled = Slot(1);
            disabled.IsGlobalPttEnabled = false;
            var (_, vm) = Create(primary: () => null, selected: () => new[] { disabled });
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.False(disabled.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(requests);
        }

        [Fact]
        public void PointerDown_PrimaryDisabledForGlobalPtt_Blocks()
        {
            var primary = Slot(1);
            primary.IsGlobalPttEnabled = false;
            var (_, vm) = Create(primary: () => primary, selected: () => new[] { primary });
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.False(primary.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(requests);
        }

        [Fact]
        public void IsGlobalPttEnabled_DefaultTrue_NotifiesOnChange()
        {
            var slot = Slot(1);

            Assert.True(slot.IsGlobalPttEnabled);

            var changes = Track(slot);
            slot.IsGlobalPttEnabled = false;

            Assert.False(slot.IsGlobalPttEnabled);
            Assert.Contains(nameof(ChannelSlotViewModel.IsGlobalPttEnabled), changes);
        }

        [Fact]
        public void EngagedTargets_ExposesPressSnapshot_AndClearsAfterRelease()
        {
            var first = Slot(1);
            var second = Slot(2);
            IReadOnlyCollection<ChannelSlotViewModel> selected = new[] { first, second };
            var (_, vm) = Create(primary: () => null, selected: () => selected);
            vm.AllChannels = true;

            vm.PttPointerDown();

            var property = typeof(PttCapabilityViewModel).GetProperty("EngagedTargets");
            Assert.NotNull(property);
            var snapshot = Assert.IsAssignableFrom<IReadOnlyList<ChannelSlotViewModel>>(
                property!.GetValue(vm));
            Assert.Equal(new[] { first, second }, snapshot);

            // Release must use the press-time snapshot, not a live resolver.
            selected = new[] { second };
            vm.PttPointerUp();

            Assert.False(first.PttEngaged);
            Assert.False(second.PttEngaged);
            Assert.Null(property.GetValue(vm));
        }

        [Fact]
        public void PointerDown_NoPrimaryAndNoAllChannels_NoTarget_NoOp()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => null, selected: () => new[] { slot });
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        [Fact]
        public void PointerDown_EmptySelection_NoTarget_NoOp()
        {
            var (_, vm) = Create(primary: () => null, selected: () => new ChannelSlotViewModel[0]);
            vm.AllChannels = true;
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        // ---- Momentary engagement ----------------------------------------------------

        [Fact]
        public void Momentary_PointerDown_EngagesTargetOnce()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            var slotChanges = Track(slot);
            var vmChanges = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Single(slotChanges.Where(n => n == "PttEngaged"));
            Assert.Single(vmChanges.Where(n => n == "IsEngaged"));
            Assert.Equal(new[] { true }, requests.ToArray());
        }

        [Fact]
        public void Momentary_RepeatedPointerDown_Idempotent_DoesNotReResolve()
        {
            var pressed = Slot(1);
            var later = Slot(2);
            var (_, vm) = Create(primary: () => pressed);
            var requests = TrackPttRequests(vm);
            vm.PttPointerDown();

            // The primary resolver changes while engaged; a repeated down
            // must not re-resolve or touch the new target.
            vm.PttPointerDown();

            Assert.True(pressed.PttEngaged);
            Assert.False(later.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Equal(new[] { true }, requests.ToArray());
        }

        [Fact]
        public void Momentary_PointerUp_ReleasesTargetOnce()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            vm.PttPointerDown();
            var slotChanges = Track(slot);
            var vmChanges = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerUp();

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Single(slotChanges.Where(n => n == "PttEngaged"));
            Assert.Single(vmChanges.Where(n => n == "IsEngaged"));
            Assert.Equal(new[] { false }, requests.ToArray());
        }

        [Fact]
        public void Momentary_RepeatedPointerUp_Idempotent()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            vm.PttPointerDown();
            vm.PttPointerUp();
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerUp();

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        [Fact]
        public void Momentary_PointerUpWithoutDown_NoOp()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerUp();

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        // ---- Toggle engagement ---------------------------------------------------------

        [Fact]
        public void Toggle_PointerDown_Engages_NextPointerDown_Releases()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            vm.ToggleMode = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();
            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);

            vm.PttPointerDown();
            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);

            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        [Fact]
        public void Toggle_PointerUp_NoOp()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            vm.ToggleMode = true;
            vm.PttPointerDown();
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.PttPointerUp();

            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        [Fact]
        public void Toggle_NoTarget_NoOp()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => null, selected: () => new[] { slot });
            vm.ToggleMode = true;
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();
            vm.PttPointerDown();

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(requests);
        }

        // ---- Hotkey event routing --------------------------------------------------------

        [Fact]
        public void Hotkey_Momentary_PressedEngages_ReleasedReleases()
        {
            var slot = Slot(1);
            var (hotkeys, vm) = Create(primary: () => slot);
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            var requests = TrackPttRequests(vm);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);
            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);

            // Released while released is idempotent.
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);
            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        [Fact]
        public void Hotkey_Toggle_PressedToggles_ReleasedNoOp()
        {
            var slot = Slot(1);
            var (hotkeys, vm) = Create(primary: () => slot);
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            vm.ToggleMode = true;
            var requests = TrackPttRequests(vm);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);

            // Released is a no-op in toggle mode.
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);
            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);

            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        [Fact]
        public void Hotkey_MismatchedGesture_NoOp()
        {
            var slot = Slot(1);
            var (hotkeys, vm) = Create(primary: () => slot);
            var configured = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(configured, HotkeyCapability.Available);
            vm.SetHotkey(configured);
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);

            vm.ApplyHotkeyPress(new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.None), HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(new HotkeyGesture(HotkeyKey.F10, HotkeyModifiers.Control), HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control | HotkeyModifiers.Shift), HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(configured, HotkeyEventType.Released);

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        [Fact]
        public void Hotkey_NoHotkeyConfigured_NoOp()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            var changes = Track(vm);
            var requests = TrackPttRequests(vm);
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);

            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Empty(changes);
            Assert.Empty(requests);
        }

        [Fact]
        public void Hotkey_PressedWhilePointerEngaged_Idempotent_ReleasedReleases()
        {
            var slot = Slot(1);
            var (hotkeys, vm) = Create(primary: () => slot);
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);
            vm.SetHotkey(gesture);
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);

            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);
            Assert.False(slot.PttEngaged);
            Assert.False(vm.IsEngaged);

            // The pointer up after a hotkey release is idempotent.
            vm.PttPointerUp();
            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        // ---- Press-time snapshot release --------------------------------------------------

        [Fact]
        public void PointerUp_AfterPrimaryChanged_ReleasesOnlyPressedSlot()
        {
            var pressed = Slot(1);
            var later = Slot(2);
            ChannelSlotViewModel? currentPrimary = pressed;
            var (_, vm) = Create(primary: () => currentPrimary);
            var requests = TrackPttRequests(vm);
            vm.PttPointerDown();

            // The primary resolver changes before release.
            currentPrimary = later;
            vm.PttPointerUp();

            Assert.False(pressed.PttEngaged);
            Assert.False(later.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        [Fact]
        public void PointerUp_AllChannels_AfterSelectionChanged_ReleasesOnlyPressedSlots()
        {
            var pressedFirst = Slot(1);
            var pressedSecond = Slot(2);
            var later = Slot(3);
            IReadOnlyCollection<ChannelSlotViewModel> currentSelection = new[] { pressedFirst, pressedSecond };
            var (_, vm) = Create(primary: () => null, selected: () => currentSelection);
            vm.AllChannels = true;
            var requests = TrackPttRequests(vm);
            vm.PttPointerDown();

            // The selection resolver changes before release.
            currentSelection = new[] { later };
            vm.PttPointerUp();

            Assert.False(pressedFirst.PttEngaged);
            Assert.False(pressedSecond.PttEngaged);
            Assert.False(later.PttEngaged);
            Assert.False(vm.IsEngaged);
            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        // ---- Duplicate targets -------------------------------------------------------------

        [Fact]
        public void PointerDown_DuplicateTargetReferences_EngageAndRequestOnce()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => null, selected: () => new[] { slot, slot });
            vm.AllChannels = true;
            var slotChanges = Track(slot);
            var requests = TrackPttRequests(vm);

            vm.PttPointerDown();

            Assert.True(slot.PttEngaged);
            Assert.True(vm.IsEngaged);
            Assert.Single(slotChanges.Where(n => n == "PttEngaged"));
            Assert.Equal(new[] { true }, requests.ToArray());

            vm.PttPointerUp();

            Assert.False(slot.PttEngaged);
            Assert.Equal(new[] { true, false }, requests.ToArray());
        }

        // ---- Change-only notifications -------------------------------------------------------

        [Fact]
        public void ToggleMode_ChangeOnly_Notifications()
        {
            var (_, vm) = Create();
            var changes = Track(vm);

            vm.ToggleMode = true;
            vm.ToggleMode = true;
            vm.ToggleMode = false;
            vm.ToggleMode = false;

            Assert.Equal(
                new[] { "ToggleMode", "ToggleMode" },
                changes.Where(n => n == "ToggleMode").ToArray());
        }

        [Fact]
        public void AllChannels_ChangeOnly_Notifications()
        {
            var (_, vm) = Create();
            var changes = Track(vm);

            vm.AllChannels = true;
            vm.AllChannels = true;
            vm.AllChannels = false;
            vm.AllChannels = false;

            Assert.Equal(
                new[] { "AllChannels", "AllChannels" },
                changes.Where(n => n == "AllChannels").ToArray());
        }

        [Fact]
        public void IsEngaged_ChangeOnly_Notifications()
        {
            var slot = Slot(1);
            var (_, vm) = Create(primary: () => slot);
            var changes = Track(vm);

            vm.PttPointerDown();
            vm.PttPointerDown();
            vm.PttPointerUp();
            vm.PttPointerUp();

            Assert.Equal(
                new[] { "IsEngaged", "IsEngaged" },
                changes.Where(n => n == "IsEngaged").ToArray());
        }

        [Fact]
        public void ChannelSlot_PttEngaged_DefaultFalse_ChangeOnly()
        {
            var slot = Slot(1);
            var changes = Track(slot);

            Assert.False(slot.PttEngaged);

            slot.PttEngaged = false;
            slot.PttEngaged = true;
            slot.PttEngaged = true;
            slot.PttEngaged = false;
            slot.PttEngaged = false;

            Assert.Equal(
                new[] { "PttEngaged", "PttEngaged" },
                changes.Where(n => n == "PttEngaged").ToArray());
        }

        // ---- Purity -------------------------------------------------------------------------

        /// <summary>
        /// A full interaction cycle — configure, hotkey press/release,
        /// momentary and toggle pointer presses, clear — must never touch
        /// the service beyond capability queries: no registration, no
        /// unregistration, no disposal.
        /// </summary>
        [Fact]
        public void Purity_FullInteraction_NoRegistrationUnregistrationOrDisposal()
        {
            var slot = Slot(1);
            var (hotkeys, vm) = Create(primary: () => slot);
            var gesture = new HotkeyGesture(HotkeyKey.F9, HotkeyModifiers.Control);
            hotkeys.SetCapability(gesture, HotkeyCapability.Available);

            vm.SetHotkey(gesture);
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(gesture, HotkeyEventType.Released);
            vm.PttPointerDown();
            vm.PttPointerUp();
            vm.ToggleMode = true;
            vm.PttPointerDown();
            vm.PttPointerUp();
            vm.PttPointerDown();
            vm.ClearHotkey();

            Assert.Equal(0, hotkeys.RegisterCalls);
            Assert.Equal(0, hotkeys.UnregisterCalls);
            Assert.Equal(0, hotkeys.DisposeCalls);
            // One capability query per successful SetHotkey, none elsewhere.
            Assert.Equal(1, hotkeys.GetCapabilityCalls);
        }
    }
}
