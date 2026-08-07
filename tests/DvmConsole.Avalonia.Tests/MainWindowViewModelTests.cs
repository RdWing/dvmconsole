// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the pure managed operator-dashboard view-models:
* DvmConsole.Avalonia.ViewModels.MainWindowViewModel and its
* ChannelSlotViewModel. These facts are written entirely against the agreed
* contract: fixed product name, OFFLINE / "Awaiting FNE configuration"
* initial connection state with CanConnect true, exactly four channel slots
* numbered 1..4 with CHANNEL 01..CHANNEL 04 labels and NO TALKGROUP / IDLE /
* non-primary defaults, and SetConnectionState semantics (all three
* connection properties plus CanConnect updated, exact nonblank string
* preservation, idempotent values, PropertyChanged raised for
* ConnectionLabel, ConnectionDetail, IsConnected, CanConnect in that order,
* and ArgumentException on null or whitespace label/detail).
*
* Channel-selection contract (RED phase for the bounded selection slice):
* a fresh dashboard has an empty SelectedChannels collection and a null
* PrimaryChannel; ProcessChannelClick(int slotNumber, bool setPrimary)
* mirrors the WPF ProcessSelectionClick semantics through the Core
* SelectedChannelsManager<ChannelSlotViewModel> - a plain click on an
* unselected slot selects it, a plain click on a selected slot deselects
* it, setPrimary=true on a selected slot sets or moves the primary,
* setPrimary=true on an unselected slot selects it only (never sets the
* primary, deselecting the primary slot clears the primary, and a primary
* click on the already-primary slot toggles primary off as in WPF.
* IsSelected/IsPrimary notify on change only, slot
* numbers outside 1..4 throw ArgumentOutOfRangeException, and
* SelectedChannels returns a detached snapshot. The slot view-model
* implements INotifyPropertyChanged with writable IsSelected/IsPrimary
* while Number/Label/Talkgroup/Status stay read-only.
*
* Audio-settings composition contract (RED phase for the bounded
* composition slice): the parameterless constructor stays catalog-free —
* AudioSettings is null and no catalog method is ever consulted — with
* the OFFLINE state, four channel slots and empty FneConnections
* untouched. The exact two-parameter constructor
* (IReadOnlyList<Codeplug.System>?, IAudioDeviceCatalog?) composes a
* get-only AudioSettingsViewModel from the injected catalog: stable
* across reads, system-default row first then catalog devices in source
* order, independent of the FNE manager; a null catalog yields
* AudioSettings null. The composition adds no IDisposable surface and no
* event subscription requirement — the audio slice must work with zero
* wiring beyond the constructor argument.
*
* PTT-capability composition contract (RED phase for the bounded PTT
* composition slice): the parameterless constructor keeps Ptt null and
* the offline dashboard unchanged — no hotkey service is ever created,
* held, or queried. The exact three-parameter constructor
* (IReadOnlyList<Codeplug.System>?, IAudioDeviceCatalog?,
* IGlobalHotkeyService?) composes a get-only PttCapabilityViewModel
* from the injected service, wired to the LIVE dashboard selection:
* its primary resolver returns the current PrimaryChannel and its
* selected resolver the current SelectedChannels snapshot, both
* resolved at press time. A null service yields a null Ptt while the
* systems/catalog composition stays independent. The composition adds
* no IDisposable surface, performs no service query until the slice's
* SetHotkey is called, and leaves the existing one- and two-argument
* null calls binding to their exact constructors.
*
* The tests are fully headless and pure managed: no Avalonia.Headless
* package, window, display, native call, file, or secret is involved.
*
* GREEN contract gate: the production view-models implement this exact
* contract without Avalonia, protocol, audio, or network coupling.
*/
#nullable enable
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>MainWindowViewModel</c> and
    /// <c>ChannelSlotViewModel</c> against the operator-dashboard contract.
    /// </summary>
    public sealed class MainWindowViewModelTests
    {
        // ---- Initial state ----------------------------------------------------

        /// <summary>
        /// A fresh view-model presents the offline dashboard: fixed product
        /// name, OFFLINE connection label, awaiting-configuration detail,
        /// disconnected, and connectable.
        /// </summary>
        [Fact]
        public void InitialState_ConnectionProperties()
        {
            var vm = new MainWindowViewModel();

            Assert.Equal("DVM Console", vm.ProductName);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal("Awaiting FNE configuration", vm.ConnectionDetail);
            Assert.False(vm.IsConnected);
            Assert.True(vm.CanConnect);
        }

        /// <summary>
        /// The dashboard starts with exactly four channel slots, numbered and
        /// labelled CHANNEL 01..04, with NO TALKGROUP / IDLE / non-primary
        /// defaults.
        /// </summary>
        [Theory]
        [InlineData(1, "CHANNEL 01")]
        [InlineData(2, "CHANNEL 02")]
        [InlineData(3, "CHANNEL 03")]
        [InlineData(4, "CHANNEL 04")]
        public void InitialState_ChannelSlots(int number, string label)
        {
            var vm = new MainWindowViewModel();

            Assert.Equal(4, vm.Channels.Count);
            var slot = vm.Channels[number - 1];
            Assert.Equal(number, slot.Number);
            Assert.Equal(label, slot.Label);
            Assert.Equal("NO TALKGROUP", slot.Talkgroup);
            Assert.Equal("IDLE", slot.Status);
            Assert.False(slot.IsPrimary);
        }

        // ---- SetConnectionState: transitions ------------------------------------

        /// <summary>
        /// A connected transition updates every connection property and
        /// raises PropertyChanged in the locked order: ConnectionLabel,
        /// ConnectionDetail, IsConnected, CanConnect.
        /// </summary>
        [Fact]
        public void SetConnectionState_Connected_UpdatesAllAndRaisesInOrder()
        {
            var vm = new MainWindowViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SetConnectionState("LINKED", "FNE-7 127.0.0.1:54000", isConnected: true);

            Assert.Equal("LINKED", vm.ConnectionLabel);
            Assert.Equal("FNE-7 127.0.0.1:54000", vm.ConnectionDetail);
            Assert.True(vm.IsConnected);
            Assert.False(vm.CanConnect);
            Assert.Equal(
                new List<string?>
                {
                    "ConnectionLabel",
                    "ConnectionDetail",
                    "IsConnected",
                    "CanConnect",
                },
                raised);
        }

        /// <summary>
        /// An offline transition flips every connection property back and
        /// raises PropertyChanged in the same locked order.
        /// </summary>
        [Fact]
        public void SetConnectionState_Offline_UpdatesAllAndRaisesInOrder()
        {
            var vm = new MainWindowViewModel();
            vm.SetConnectionState("LINKED", "FNE-7 127.0.0.1:54000", isConnected: true);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SetConnectionState("OFFLINE", "Awaiting FNE configuration", isConnected: false);

            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal("Awaiting FNE configuration", vm.ConnectionDetail);
            Assert.False(vm.IsConnected);
            Assert.True(vm.CanConnect);
            Assert.Equal(
                new List<string?>
                {
                    "ConnectionLabel",
                    "ConnectionDetail",
                    "IsConnected",
                    "CanConnect",
                },
                raised);
        }

        // ---- SetConnectionState: exactness and idempotency -----------------------

        /// <summary>
        /// Nonblank strings are preserved verbatim, including surrounding
        /// whitespace, and re-applying identical values is idempotent in
        /// value while notifications are still raised on every call.
        /// </summary>
        [Fact]
        public void SetConnectionState_PreservesExactStrings_AndIsIdempotentInValue()
        {
            var vm = new MainWindowViewModel();
            vm.PropertyChanged += (_, _) => { };

            vm.SetConnectionState("  LINKED  ", "FNE-7  [TCP]  ", isConnected: true);

            Assert.Equal("  LINKED  ", vm.ConnectionLabel);
            Assert.Equal("FNE-7  [TCP]  ", vm.ConnectionDetail);
            Assert.True(vm.IsConnected);

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SetConnectionState("  LINKED  ", "FNE-7  [TCP]  ", isConnected: true);

            Assert.Equal("  LINKED  ", vm.ConnectionLabel);
            Assert.Equal("FNE-7  [TCP]  ", vm.ConnectionDetail);
            Assert.True(vm.IsConnected);
            Assert.False(vm.CanConnect);
            Assert.Equal(4, raised.Count);
        }

        // ---- SetConnectionState: invalid arguments --------------------------------

        /// <summary>
        /// A null or whitespace-only connection label is a programming error
        /// and must be rejected with ArgumentException.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SetConnectionState_InvalidLabel_ThrowsArgumentException(string? label)
        {
            var vm = new MainWindowViewModel();

            Assert.Throws<ArgumentException>(
                () => vm.SetConnectionState(label!, "detail", isConnected: false));
        }

        /// <summary>
        /// A null or whitespace-only connection detail is a programming error
        /// and must be rejected with ArgumentException.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SetConnectionState_InvalidDetail_ThrowsArgumentException(string? detail)
        {
            var vm = new MainWindowViewModel();

            Assert.Throws<ArgumentException>(
                () => vm.SetConnectionState("OFFLINE", detail!, isConnected: false));
        }

        // ---- Channel selection: initial state --------------------------------------

        /// <summary>
        /// A fresh view-model has no selected channels and no primary
        /// channel; every slot starts unselected and non-primary.
        /// </summary>
        [Fact]
        public void InitialState_NoSelection_NoPrimary()
        {
            var vm = new MainWindowViewModel();
            INotifyPropertyChanged notifying = vm.Channels[0]; // compile-lock: slot is observable

            Assert.Empty(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);
            foreach (var slot in vm.Channels)
            {
                Assert.False(slot.IsSelected);
                Assert.False(slot.IsPrimary);
            }

            Assert.Same(notifying, vm.Channels[0]);
        }

        // ---- Channel selection: select / deselect -----------------------------------

        /// <summary>
        /// Clicking an unselected slot selects it: the slot flips to
        /// IsSelected and appears in SelectedChannels, and no primary is
        /// implied.
        /// </summary>
        [Fact]
        public void SelectClick_UnselectedSlot_SelectsIt()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            Assert.True(slot.IsSelected);
            Assert.Single(vm.SelectedChannels);
            Assert.Same(slot, Assert.Single(vm.SelectedChannels));
            Assert.Null(vm.PrimaryChannel);
        }

        /// <summary>
        /// Clicking a selected slot (plain click) deselects it.
        /// </summary>
        [Fact]
        public void SelectClick_SelectedSlot_DeselectsIt()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            Assert.False(slot.IsSelected);
            Assert.Empty(vm.SelectedChannels);
        }

        /// <summary>
        /// Selections are independent: selecting two slots keeps both
        /// selected, and deselecting one leaves the other selected.
        /// </summary>
        [Fact]
        public void SelectClick_MultipleSlots_SelectionsAreIndependent()
        {
            var vm = new MainWindowViewModel();
            var first = vm.Channels[0];
            var third = vm.Channels[2];

            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(third.Number, setPrimary: false);
            vm.ProcessChannelClick(first.Number, setPrimary: false);

            Assert.False(first.IsSelected);
            Assert.True(third.IsSelected);
            Assert.Single(vm.SelectedChannels);
            Assert.Same(third, Assert.Single(vm.SelectedChannels));
        }

        // ---- Channel selection: primary set / clear / move ---------------------------

        /// <summary>
        /// setPrimary=true on a selected slot makes it the primary
        /// channel: PrimaryChannel points at it and its IsPrimary flips
        /// true, while the selection is untouched.
        /// </summary>
        [Fact]
        public void PrimaryClick_SelectedSlot_SetsPrimary()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            Assert.Same(slot, vm.PrimaryChannel);
            Assert.True(slot.IsPrimary);
            Assert.True(slot.IsSelected);
            Assert.Single(vm.SelectedChannels);
        }

        /// <summary>
        /// setPrimary=true on a second selected slot moves the primary:
        /// the new slot becomes primary and the previous primary is
        /// demoted, while both stay selected.
        /// </summary>
        [Fact]
        public void PrimaryClick_SelectedSlot_MovesPrimaryFromAnotherSlot()
        {
            var vm = new MainWindowViewModel();
            var first = vm.Channels[0];
            var second = vm.Channels[1];
            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(second.Number, setPrimary: false);
            vm.ProcessChannelClick(first.Number, setPrimary: true);

            vm.ProcessChannelClick(second.Number, setPrimary: true);

            Assert.Same(second, vm.PrimaryChannel);
            Assert.True(second.IsPrimary);
            Assert.False(first.IsPrimary);
            Assert.True(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Equal(2, vm.SelectedChannels.Count);
        }

        /// <summary>
        /// Deselecting the primary slot (plain click) removes it from the
        /// selection AND clears the primary channel in the same operation.
        /// </summary>
        [Fact]
        public void PlainClick_PrimarySlot_DeselectsAndClearsPrimary()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            Assert.False(slot.IsSelected);
            Assert.False(slot.IsPrimary);
            Assert.Empty(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);
        }

        // ---- Channel selection: unselected-primary quirk ------------------------------

        /// <summary>
        /// setPrimary=true on an UNSELECTED slot selects it only: the
        /// primary is NOT set and the slot stays non-primary (the WPF
        /// Ctrl-click branch only applies to already-selected slots).
        /// </summary>
        [Fact]
        public void PrimaryClick_UnselectedSlot_SelectsOnly_DoesNotSetPrimary()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];

            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            Assert.True(slot.IsSelected);
            Assert.False(slot.IsPrimary);
            Assert.Single(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);
        }

        // ---- Channel selection: primary toggle ------------------------------------------

        /// <summary>
        /// A primary click on the slot that is already primary toggles the
        /// primary state off, matching the WPF Ctrl-click behavior while
        /// leaving membership selected.
        /// </summary>
        [Fact]
        public void PrimaryClick_AlreadyPrimary_TogglesPrimaryOff()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            Assert.Null(vm.PrimaryChannel);
            Assert.False(slot.IsPrimary);
            Assert.True(slot.IsSelected);
            Assert.Single(vm.SelectedChannels);
        }

        // ---- Channel selection: notifications -------------------------------------------

        /// <summary>
        /// Selecting an unselected slot raises PropertyChanged for
        /// IsSelected exactly once, and nothing else.
        /// </summary>
        [Fact]
        public void SelectClick_RaisesIsSelectedNotification()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            var raised = new List<string?>();
            slot.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            Assert.Equal(new List<string?> { "IsSelected" }, raised);
        }

        /// <summary>
        /// Deselecting a selected slot raises PropertyChanged for
        /// IsSelected exactly once, and nothing else.
        /// </summary>
        [Fact]
        public void DeselectClick_RaisesIsSelectedNotification()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            var raised = new List<string?>();
            slot.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            Assert.Equal(new List<string?> { "IsSelected" }, raised);
        }

        /// <summary>
        /// Setting the primary raises PropertyChanged for IsPrimary on
        /// the slot exactly once, and does not re-raise IsSelected.
        /// </summary>
        [Fact]
        public void PrimaryClick_RaisesIsPrimaryNotification()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            var raised = new List<string?>();
            slot.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ProcessChannelClick(slot.Number, setPrimary: true);

            Assert.Equal(new List<string?> { "IsPrimary" }, raised);
        }

        /// <summary>
        /// Moving the primary raises IsPrimary on both slots: false on
        /// the demoted slot and true on the new primary.
        /// </summary>
        [Fact]
        public void PrimaryMove_RaisesIsPrimaryOnBothSlots()
        {
            var vm = new MainWindowViewModel();
            var first = vm.Channels[0];
            var second = vm.Channels[1];
            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(second.Number, setPrimary: false);
            vm.ProcessChannelClick(first.Number, setPrimary: true);
            var firstRaised = new List<string?>();
            var secondRaised = new List<string?>();
            first.PropertyChanged += (_, e) => firstRaised.Add(e.PropertyName);
            second.PropertyChanged += (_, e) => secondRaised.Add(e.PropertyName);

            vm.ProcessChannelClick(second.Number, setPrimary: true);

            Assert.Equal(new List<string?> { "IsPrimary" }, firstRaised);
            Assert.Equal(new List<string?> { "IsPrimary" }, secondRaised);
        }

        /// <summary>
        /// Deselecting the primary raises IsSelected (false) and
        /// IsPrimary (false) on the slot; the contract does not lock
        /// their relative order.
        /// </summary>
        [Fact]
        public void PlainClick_PrimarySlot_RaisesIsSelectedAndIsPrimary()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            vm.ProcessChannelClick(slot.Number, setPrimary: true);
            var raised = new List<string?>();
            slot.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            raised.Sort(StringComparer.Ordinal);
            Assert.Equal(new List<string?> { "IsPrimary", "IsSelected" }, raised);
        }

        // ---- Channel selection: invalid slot numbers ------------------------------------

        /// <summary>
        /// Slot numbers outside the valid 1..4 range are programming
        /// errors and must be rejected with
        /// ArgumentOutOfRangeException, both below and above the range.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(5)]
        [InlineData(99)]
        public void ProcessChannelClick_InvalidSlotNumber_ThrowsArgumentOutOfRangeException(int slotNumber)
        {
            var vm = new MainWindowViewModel();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => vm.ProcessChannelClick(slotNumber, setPrimary: false));
        }

        // ---- Channel selection: detached snapshot -----------------------------------------

        /// <summary>
        /// SelectedChannels returns a detached snapshot: mutating the
        /// returned collection never affects the view-model selection.
        /// </summary>
        [Fact]
        public void SelectedChannels_ReturnsDetachedSnapshot_MutationDoesNotAffectVm()
        {
            var vm = new MainWindowViewModel();
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);

            var snapshot = vm.SelectedChannels;
            try
            {
                (snapshot as ICollection<ChannelSlotViewModel>)?.Clear();
            }
            catch (NotSupportedException)
            {
                // A future detached snapshot may be read-only; that is the contract.
            }

            Assert.Single(vm.SelectedChannels);
            Assert.Same(slot, Assert.Single(vm.SelectedChannels));
        }

        /// <summary>
        /// Two calls to SelectedChannels return distinct collection
        /// instances, never the same live object.
        /// </summary>
        [Fact]
        public void SelectedChannels_TwoCalls_DistinctInstances()
        {
            var vm = new MainWindowViewModel();
            vm.ProcessChannelClick(vm.Channels[0].Number, setPrimary: false);

            var first = vm.SelectedChannels;
            var second = vm.SelectedChannels;

            Assert.NotSame(first, second);
        }

        // ---- Compile-time shape ----------------------------------------------------

        /// <summary>
        /// Shape gate for the members the XAML shell relies on: sealed types,
        /// exact property types, read-only slots, the PropertyChanged event,
        /// and the SetConnectionState signature. Locked reflectively so a
        /// signature drift fails loudly. Channel-selection members are locked
        /// too: the slot is observable with writable IsSelected/IsPrimary,
        /// the read-only identity members stay read-only, and the
        /// ProcessChannelClick(int, bool) entry point plus the
        /// SelectedChannels / PrimaryChannel properties have exact types.
        /// </summary>
        [Fact]
        public void ViewModelShape_SealedTypesAndExactMemberSignatures()
        {
            var main = typeof(MainWindowViewModel);

            Assert.True(main.IsSealed);
            Assert.Equal(typeof(string), main.GetProperty(nameof(MainWindowViewModel.ProductName))!.PropertyType);
            Assert.Equal(typeof(string), main.GetProperty(nameof(MainWindowViewModel.ConnectionLabel))!.PropertyType);
            Assert.Equal(typeof(string), main.GetProperty(nameof(MainWindowViewModel.ConnectionDetail))!.PropertyType);
            Assert.Equal(typeof(bool), main.GetProperty(nameof(MainWindowViewModel.IsConnected))!.PropertyType);
            Assert.Equal(typeof(bool), main.GetProperty(nameof(MainWindowViewModel.CanConnect))!.PropertyType);
            Assert.Equal(
                typeof(IReadOnlyList<ChannelSlotViewModel>),
                main.GetProperty(nameof(MainWindowViewModel.Channels))!.PropertyType);
            Assert.Equal(
                typeof(IReadOnlyCollection<ChannelSlotViewModel>),
                main.GetProperty(nameof(MainWindowViewModel.SelectedChannels))!.PropertyType);
            Assert.Equal(
                typeof(ChannelSlotViewModel),
                main.GetProperty(nameof(MainWindowViewModel.PrimaryChannel))!.PropertyType);

            var setState = main.GetMethod(
                nameof(MainWindowViewModel.SetConnectionState),
                new[] { typeof(string), typeof(string), typeof(bool) });
            Assert.NotNull(setState);
            Assert.Equal(typeof(void), setState!.ReturnType);

            var processClick = main.GetMethod(
                nameof(MainWindowViewModel.ProcessChannelClick),
                new[] { typeof(int), typeof(bool) });
            Assert.NotNull(processClick);
            Assert.Equal(typeof(void), processClick!.ReturnType);

            var changed = main.GetEvent(nameof(MainWindowViewModel.PropertyChanged));
            Assert.NotNull(changed);
            Assert.Equal(typeof(PropertyChangedEventHandler), changed!.EventHandlerType);

            var slot = typeof(ChannelSlotViewModel);
            Assert.True(slot.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(slot));
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Number))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Label))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.ChannelName))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.CanWrite);
            Assert.True(slot.GetProperty(nameof(ChannelSlotViewModel.IsSelected))!.CanWrite);
            Assert.True(slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.CanWrite);
            Assert.Equal(typeof(int), slot.GetProperty(nameof(ChannelSlotViewModel.Number))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Label))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.ChannelName))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.PropertyType);
            Assert.Equal(typeof(bool), slot.GetProperty(nameof(ChannelSlotViewModel.IsSelected))!.PropertyType);
            Assert.Equal(typeof(bool), slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.PropertyType);

            // Zone UI surface (audit deleg_9f6f3919).
            Assert.Equal(
                typeof(IReadOnlyList<ZoneViewModel>),
                main.GetProperty(nameof(MainWindowViewModel.Zones))!.PropertyType);
            Assert.Equal(
                typeof(ZoneViewModel),
                main.GetProperty(nameof(MainWindowViewModel.SelectedZone))!.PropertyType);
            var selectedZoneSetter = main.GetProperty(nameof(MainWindowViewModel.SelectedZone))!.SetMethod;
            Assert.NotNull(selectedZoneSetter);
        }

        // ---- Audio settings composition: fixture ----------------------------------

        /// <summary>
        /// Immutable, headless <see cref="IAudioDeviceCatalog"/> fake: the
        /// direction lists are supplied snapshots, defaults are looked up
        /// by the <see cref="AudioDeviceId.IsDefault"/> marker, ids resolve
        /// case-insensitively across both directions, and disposal is
        /// completed. A static access counter lets tests prove the
        /// parameterless constructor never touches a catalog. No events
        /// and no native code — the slice under test must never depend on
        /// either.
        /// </summary>
        private sealed class FakeAudioDeviceCatalog : IAudioDeviceCatalog
        {
            /// <summary>Total catalog method invocations since the last reset.</summary>
            public static int AccessCount { get; private set; }

            /// <summary>Resets <see cref="AccessCount"/> to zero.</summary>
            public static void ResetAccessCount() => AccessCount = 0;

            private readonly IReadOnlyList<AudioDeviceInfo> inputs;
            private readonly IReadOnlyList<AudioDeviceInfo> outputs;

            /// <summary>
            /// Creates a fake serving the given snapshot lists (empty when
            /// null).
            /// </summary>
            public FakeAudioDeviceCatalog(
                IReadOnlyList<AudioDeviceInfo>? inputs = null,
                IReadOnlyList<AudioDeviceInfo>? outputs = null)
            {
                this.inputs = inputs ?? Array.Empty<AudioDeviceInfo>();
                this.outputs = outputs ?? Array.Empty<AudioDeviceInfo>();
            }

            public IReadOnlyList<AudioDeviceInfo> GetInputs()
            {
                AccessCount++;
                return inputs;
            }

            public IReadOnlyList<AudioDeviceInfo> GetOutputs()
            {
                AccessCount++;
                return outputs;
            }

            public AudioDeviceInfo? GetDefaultInput()
            {
                AccessCount++;
                return inputs.FirstOrDefault(d => d.Id.IsDefault);
            }

            public AudioDeviceInfo? GetDefaultOutput()
            {
                AccessCount++;
                return outputs.FirstOrDefault(d => d.Id.IsDefault);
            }

            public bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device)
            {
                AccessCount++;
                if (id.IsDefault)
                {
                    device = GetDefaultOutput() ?? GetDefaultInput();
                    return device is not null;
                }

                device = inputs.Concat(outputs).FirstOrDefault(d =>
                    string.Equals(d.Id.Value, id.Value, StringComparison.OrdinalIgnoreCase));
                return device is not null;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        // ---- Audio settings composition: parameterless constructor -------------------

        /// <summary>
        /// The parameterless constructor stays catalog-free: AudioSettings
        /// is null, no catalog method is ever consulted, and the offline
        /// dashboard (OFFLINE label, four channel slots, empty FNE
        /// manager) is untouched.
        /// </summary>
        [Fact]
        public void DefaultCtor_AudioSettingsNull_NoCatalogAccess()
        {
            FakeAudioDeviceCatalog.ResetAccessCount();

            var vm = new MainWindowViewModel();

            Assert.Null(vm.AudioSettings);
            Assert.Equal(0, FakeAudioDeviceCatalog.AccessCount);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal("Awaiting FNE configuration", vm.ConnectionDetail);
            Assert.Equal(4, vm.Channels.Count);
            Assert.False(vm.FneConnections.HasSystems);
        }

        // ---- Audio settings composition: two-parameter constructor -------------------

        /// <summary>
        /// The two-parameter constructor composes a live
        /// AudioSettingsViewModel from the injected catalog: the property
        /// is stable across reads, and each direction lists the
        /// system-default row first followed by the catalog devices in
        /// source order.
        /// </summary>
        [Fact]
        public void TwoArgCtor_WithCatalog_AudioSettingsComposed()
        {
            var catalog = new FakeAudioDeviceCatalog(
                inputs: new[]
                {
                    new AudioDeviceInfo(AudioDeviceId.FromKey("mic-1"), AudioDeviceDirection.Input, "Built-in Microphone"),
                    new AudioDeviceInfo(AudioDeviceId.FromKey("mic-2"), AudioDeviceDirection.Input, "USB Microphone"),
                },
                outputs: new[]
                {
                    new AudioDeviceInfo(AudioDeviceId.FromKey("spk-1"), AudioDeviceDirection.Output, "Built-in Speakers"),
                });

            var vm = new MainWindowViewModel(null, catalog);

            Assert.NotNull(vm.AudioSettings);
            Assert.Same(vm.AudioSettings, vm.AudioSettings);

            Assert.Equal(3, vm.AudioSettings.InputDevices.Count);
            Assert.True(vm.AudioSettings.InputDevices[0].Id.IsDefault);
            Assert.Equal("mic-1", vm.AudioSettings.InputDevices[1].Id.Value);
            Assert.Equal("Built-in Microphone", vm.AudioSettings.InputDevices[1].Name);
            Assert.Equal("mic-2", vm.AudioSettings.InputDevices[2].Id.Value);

            Assert.Equal(2, vm.AudioSettings.OutputDevices.Count);
            Assert.True(vm.AudioSettings.OutputDevices[0].Id.IsDefault);
            Assert.Equal("spk-1", vm.AudioSettings.OutputDevices[1].Id.Value);
            Assert.Equal("Built-in Speakers", vm.AudioSettings.OutputDevices[1].Name);
        }

        /// <summary>
        /// Systems and catalog compose independently through the
        /// two-parameter constructor: the FNE manager is seeded from the
        /// codeplug systems while AudioSettings snapshots the catalog;
        /// neither affects the other.
        /// </summary>
        [Fact]
        public void TwoArgCtor_SystemsAndCatalog_ComposeIndependently()
        {
            var catalog = new FakeAudioDeviceCatalog(
                inputs: new[]
                {
                    new AudioDeviceInfo(AudioDeviceId.FromKey("mic-1"), AudioDeviceDirection.Input, "Built-in Microphone"),
                });

            var vm = new MainWindowViewModel(
                new[]
                {
                    new Codeplug.System
                    {
                        Name = "TEST-NET",
                        Identity = "TEST-CALLSIGN",
                        Address = "127.0.0.1",
                        Port = 54000,
                        PeerId = 1u,
                        Encrypted = true,
                    },
                },
                catalog);

            Assert.True(vm.FneConnections.HasSystems);
            Assert.Single(vm.FneConnections.Systems);
            Assert.Equal("TEST-NET", vm.FneConnections.Systems[0].SystemName);

            Assert.NotNull(vm.AudioSettings);
            Assert.Equal(2, vm.AudioSettings.InputDevices.Count);
            Assert.Equal("mic-1", vm.AudioSettings.InputDevices[1].Id.Value);
        }

        /// <summary>
        /// Null-catalog compatibility: (null, null) yields a valid
        /// dashboard whose AudioSettings is null, with the offline state,
        /// four channel slots and an empty FNE manager intact.
        /// </summary>
        [Fact]
        public void TwoArgCtor_NullCatalog_AudioSettingsNull()
        {
            var vm = new MainWindowViewModel(null, null);

            Assert.Null(vm.AudioSettings);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal(4, vm.Channels.Count);
            Assert.False(vm.FneConnections.HasSystems);
        }

        /// <summary>
        /// A null catalog with systems still seeds the FNE manager, while
        /// AudioSettings stays null.
        /// </summary>
        [Fact]
        public void TwoArgCtor_NullCatalogWithSystems_FneComposesOnly()
        {
            var vm = new MainWindowViewModel(
                new[]
                {
                    new Codeplug.System
                    {
                        Name = "TEST-NET",
                        Identity = "TEST-CALLSIGN",
                        Address = "127.0.0.1",
                        Port = 54000,
                        PeerId = 1u,
                        Encrypted = true,
                    },
                },
                null);

            Assert.True(vm.FneConnections.HasSystems);
            Assert.Null(vm.AudioSettings);
        }

        // ---- Audio settings composition: compile-time shape ---------------------------

        /// <summary>
        /// Shape gate for the audio-settings composition surface: the
        /// AudioSettings property has the exact
        /// <c>AudioSettingsViewModel</c> type and is get-only,
        /// MainWindowViewModel stays non-disposable (no IDisposable
        /// surface, no event subscription requirement), and the exact
        /// (IReadOnlyList&lt;Codeplug.System&gt;?, IAudioDeviceCatalog?)
        /// constructor exists.
        /// </summary>
        [Fact]
        public void AudioSettingsCompositionShape_ExactPropertyAndConstructor()
        {
            var main = typeof(MainWindowViewModel);

            var audioSettings = main.GetProperty(nameof(MainWindowViewModel.AudioSettings));
            Assert.NotNull(audioSettings);
            Assert.Equal(typeof(AudioSettingsViewModel), audioSettings!.PropertyType);
            Assert.False(audioSettings.CanWrite);

            Assert.False(typeof(IDisposable).IsAssignableFrom(main));

            var compose = main.GetConstructor(
                new[] { typeof(IReadOnlyList<Codeplug.System>), typeof(IAudioDeviceCatalog) });
            Assert.NotNull(compose);
        }

        // ---- PTT composition: fixture ------------------------------------------

        /// <summary>
        /// Minimal, headless <see cref="IGlobalHotkeyService"/> fake: every
        /// capability query is counted and reports
        /// <see cref="HotkeyCapability.Unsupported"/>, registration and
        /// unregistration are no-ops that always complete, and disposal is a
        /// no-op. The press event is declared but never raised — the PTT
        /// composition slice under test only ever queries capability, and
        /// only after <c>SetHotkey</c>.
        /// </summary>
        private sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
        {
            /// <summary>Total <see cref="GetCapability"/> calls.</summary>
            public int GetCapabilityCalls { get; private set; }

#pragma warning disable CS0067 // Declared but never raised; declaration is all the contract needs.
            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
#pragma warning restore CS0067

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
            {
                GetCapabilityCalls++;
                return HotkeyCapability.Unsupported;
            }

            public Task<HotkeyRegistrationResult> RegisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
                => Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture));

            public Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public void Dispose()
            {
            }
        }

        // ---- PTT composition: parameterless constructor --------------------------

        /// <summary>
        /// The parameterless constructor stays hotkey-free: Ptt is null and
        /// the offline dashboard (OFFLINE label, four channel slots, empty
        /// FNE manager, null AudioSettings) is untouched. No hotkey service
        /// is created or queried — there is nothing to query.
        /// </summary>
        [Fact]
        public void DefaultCtor_PttNull_DashboardUnchanged()
        {
            var vm = new MainWindowViewModel();

            Assert.Null(vm.Ptt);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal("Awaiting FNE configuration", vm.ConnectionDetail);
            Assert.Equal(4, vm.Channels.Count);
            Assert.False(vm.FneConnections.HasSystems);
            Assert.Null(vm.AudioSettings);
        }

        // ---- PTT composition: three-parameter constructor -------------------------

        /// <summary>
        /// The three-parameter constructor composes a live, get-only
        /// PttCapabilityViewModel from the injected hotkey service: stable
        /// across reads and initially disengaged. The service is queried
        /// only when the slice's SetHotkey is called — construction performs
        /// zero capability lookups.
        /// </summary>
        [Fact]
        public void ThreeArgCtor_WithHotkeys_PttComposed_NoCapabilityQueryUntilSetHotkey()
        {
            var fake = new FakeGlobalHotkeyService();

            var vm = new MainWindowViewModel(null, null, fake);

            Assert.NotNull(vm.Ptt);
            Assert.Same(vm.Ptt, vm.Ptt);
            Assert.Equal(0, fake.GetCapabilityCalls);
            Assert.False(vm.Ptt.IsEngaged);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal(4, vm.Channels.Count);
            Assert.False(vm.FneConnections.HasSystems);
            Assert.Null(vm.AudioSettings);
        }

        /// <summary>
        /// Pointer engagement resolves the live primary channel at press
        /// time: pressing with a primary set engages exactly that slot
        /// (PttEngaged true, PttStateRequested(true)) and releasing clears
        /// both (PttEngaged false, PttStateRequested(false)).
        /// </summary>
        [Fact]
        public void PointerEngagement_WithPrimary_RequestsStateAndUpdatesSlots()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.Ptt);
            var slot = vm.Channels[0];
            vm.ProcessChannelClick(slot.Number, setPrimary: false);
            vm.ProcessChannelClick(slot.Number, setPrimary: true);
            var requests = new List<bool>();
            vm.Ptt.PttStateRequested += engaged => requests.Add(engaged);

            vm.Ptt.PttPointerDown();

            Assert.True(vm.Ptt.IsEngaged);
            Assert.True(slot.PttEngaged);
            Assert.Equal(new List<bool> { true }, requests);

            vm.Ptt.PttPointerUp();

            Assert.False(vm.Ptt.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Equal(new List<bool> { true, false }, requests);
        }

        /// <summary>
        /// The existing null-argument calls keep binding to their exact
        /// constructors once the three-parameter constructor exists: the
        /// third parameter is not optional, so one- and two-argument null
        /// calls stay unambiguous, and a null hotkey service yields a null
        /// Ptt.
        /// </summary>
        [Fact]
        public void NullArgumentCalls_RemainUnambiguous()
        {
            var bare = new MainWindowViewModel(null);
            var pair = new MainWindowViewModel(null, null);
            var triple = new MainWindowViewModel(null, null, null);

            Assert.Null(bare.Ptt);
            Assert.Null(pair.Ptt);
            Assert.Null(triple.Ptt);
            Assert.Equal("OFFLINE", triple.ConnectionLabel);
        }

        // ---- PTT composition: engagement targeting ---------------------------------

        /// <summary>
        /// With no primary channel and AllChannels false (the default),
        /// pointer engagement has no target: IsEngaged stays false, no
        /// PttStateRequested is raised, and no slot reports PttEngaged.
        /// </summary>
        [Fact]
        public void Ptt_NoPrimaryAndAllChannelsFalse_NoTarget()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.Ptt);
            Assert.False(vm.Ptt.AllChannels);
            var requests = new List<bool>();
            vm.Ptt.PttStateRequested += engaged => requests.Add(engaged);

            vm.Ptt.PttPointerDown();

            Assert.False(vm.Ptt.IsEngaged);
            Assert.Empty(requests);
            Assert.All(vm.Channels, slot => Assert.False(slot.PttEngaged));
        }

        /// <summary>
        /// With AllChannels true and no primary, pointer engagement engages
        /// exactly the live selected channels: each selected slot reports
        /// PttEngaged, and releasing clears each of them with a matching
        /// PttStateRequested(false).
        /// </summary>
        [Fact]
        public void Ptt_AllChannelsTrue_EngagesAllSelected()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.Ptt);
            var first = vm.Channels[0];
            var second = vm.Channels[1];
            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(second.Number, setPrimary: false);
            var requests = new List<bool>();
            vm.Ptt.PttStateRequested += engaged => requests.Add(engaged);

            vm.Ptt.AllChannels = true;
            vm.Ptt.PttPointerDown();

            Assert.True(vm.Ptt.IsEngaged);
            Assert.True(first.PttEngaged);
            Assert.True(second.PttEngaged);
            Assert.False(vm.Channels[2].PttEngaged);
            Assert.Equal(new List<bool> { true }, requests);

            vm.Ptt.PttPointerUp();

            Assert.False(vm.Ptt.IsEngaged);
            Assert.False(first.PttEngaged);
            Assert.False(second.PttEngaged);
            Assert.Equal(new List<bool> { true, false }, requests);
        }

        /// <summary>
        /// The primary channel wins over the selected channels: with a
        /// primary set and AllChannels true, engagement targets only the
        /// primary slot, never the other selected slots.
        /// </summary>
        [Fact]
        public void Ptt_PrimaryWinsOverSelectedChannels()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.Ptt);
            var first = vm.Channels[0];
            var third = vm.Channels[2];
            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(third.Number, setPrimary: false);
            vm.ProcessChannelClick(first.Number, setPrimary: true);
            vm.Ptt.AllChannels = true;

            vm.Ptt.PttPointerDown();

            Assert.True(first.PttEngaged);
            Assert.False(third.PttEngaged);
            Assert.True(vm.Ptt.IsEngaged);
        }

        /// <summary>
        /// A null hotkey service yields a null Ptt while the systems and
        /// catalog composition stays fully independent: the FNE manager is
        /// seeded from the codeplug systems and AudioSettings snapshots the
        /// catalog exactly as in the two-parameter constructor.
        /// </summary>
        [Fact]
        public void ThreeArgCtor_NullHotkeys_PttNull_SystemsAndCatalogComposeIndependently()
        {
            var catalog = new FakeAudioDeviceCatalog(
                inputs: new[]
                {
                    new AudioDeviceInfo(AudioDeviceId.FromKey("mic-1"), AudioDeviceDirection.Input, "Built-in Microphone"),
                });

            var vm = new MainWindowViewModel(
                new[]
                {
                    new Codeplug.System
                    {
                        Name = "TEST-NET",
                        Identity = "TEST-CALLSIGN",
                        Address = "127.0.0.1",
                        Port = 54000,
                        PeerId = 1u,
                        Encrypted = true,
                    },
                },
                catalog,
                null);

            Assert.Null(vm.Ptt);
            Assert.True(vm.FneConnections.HasSystems);
            Assert.Single(vm.FneConnections.Systems);
            Assert.Equal("TEST-NET", vm.FneConnections.Systems[0].SystemName);
            Assert.NotNull(vm.AudioSettings);
            Assert.Equal(2, vm.AudioSettings.InputDevices.Count);
        }

        // ---- PTT composition: live resolver wiring ---------------------------------

        /// <summary>
        /// The PTT slice is wired to the LIVE dashboard selection, not to a
        /// construction-time snapshot: selections and the primary made (or
        /// repointed) after construction are what pointer engagement
        /// resolves at press time — for both the primary resolver and the
        /// selected-channels resolver.
        /// </summary>
        [Fact]
        public void Ptt_ResolversAreLive_SelectionChangesAfterConstructionApply()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.Ptt);
            var first = vm.Channels[0];
            var second = vm.Channels[1];
            var third = vm.Channels[2];

            // Primary chosen after construction engages at press time.
            vm.ProcessChannelClick(first.Number, setPrimary: false);
            vm.ProcessChannelClick(first.Number, setPrimary: true);
            vm.Ptt.PttPointerDown();
            Assert.True(first.PttEngaged);
            Assert.False(third.PttEngaged);
            vm.Ptt.PttPointerUp();

            // Repointing the live primary after construction redirects the
            // next press; the old snapshot is not re-engaged.
            vm.ProcessChannelClick(first.Number, setPrimary: false); // deselect + clear primary
            vm.ProcessChannelClick(third.Number, setPrimary: false);
            vm.ProcessChannelClick(third.Number, setPrimary: true);
            vm.Ptt.PttPointerDown();
            Assert.True(third.PttEngaged);
            Assert.False(first.PttEngaged);
            vm.Ptt.PttPointerUp();

            // The selected-channels resolver is live too: AllChannels=true
            // set after construction engages the current selection.
            vm.ProcessChannelClick(third.Number, setPrimary: false); // deselect + clear primary
            vm.ProcessChannelClick(second.Number, setPrimary: false);
            vm.Ptt.AllChannels = true;
            vm.Ptt.PttPointerDown();
            Assert.True(second.PttEngaged);
            Assert.False(first.PttEngaged);
            Assert.False(third.PttEngaged);
            vm.Ptt.PttPointerUp();
        }

        // ---- PTT composition: compile-time shape ------------------------------------

        /// <summary>
        /// Shape gate for the PTT composition surface: the Ptt property has
        /// the exact <c>PttCapabilityViewModel</c> type and is get-only,
        /// MainWindowViewModel stays non-disposable (no IDisposable
        /// surface), and the exact
        /// (IReadOnlyList&lt;Codeplug.System&gt;?, IAudioDeviceCatalog?,
        /// IGlobalHotkeyService?) constructor exists.
        /// </summary>
        [Fact]
        public void PttCompositionShape_ExactPropertyAndConstructor()
        {
            var main = typeof(MainWindowViewModel);

            var ptt = main.GetProperty("Ptt");
            Assert.NotNull(ptt);
            Assert.Equal(typeof(PttCapabilityViewModel), ptt!.PropertyType);
            Assert.False(ptt.CanWrite);

            Assert.False(typeof(IDisposable).IsAssignableFrom(main));

            var compose = main.GetConstructor(
                new[]
                {
                    typeof(IReadOnlyList<Codeplug.System>),
                    typeof(IAudioDeviceCatalog),
                    typeof(IGlobalHotkeyService),
                });
            Assert.NotNull(compose);
        }

        // ---- Hotkey-capture composition: three-parameter constructor ------------------

        /// <summary>
        /// The three-parameter constructor also composes a get-only
        /// HotkeyCaptureViewModel from the injected hotkey service:
        /// stable across reads, initially idle, and constructed with zero
        /// service queries.
        /// </summary>
        [Fact]
        public void ThreeArgCtor_WithHotkeys_HotkeyCaptureComposed_StableAndIdle()
        {
            var fake = new FakeGlobalHotkeyService();

            var vm = new MainWindowViewModel(null, null, fake);

            Assert.NotNull(vm.HotkeyCapture);
            Assert.Same(vm.HotkeyCapture, vm.HotkeyCapture);
            Assert.False(vm.HotkeyCapture.IsCapturing);
            Assert.Equal(0, fake.GetCapabilityCalls);
        }

        /// <summary>
        /// A null hotkey service yields a null HotkeyCapture (and null
        /// Ptt) while the offline dashboard stays untouched.
        /// </summary>
        [Fact]
        public void ThreeArgCtor_NullHotkeys_HotkeyCaptureNull()
        {
            var vm = new MainWindowViewModel(null, null, null);

            Assert.Null(vm.HotkeyCapture);
            Assert.Null(vm.Ptt);
            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal(4, vm.Channels.Count);
        }

        /// <summary>
        /// The composed capture slice is wired to the composed Ptt slice:
        /// a captured gesture reaches Ptt exactly once (HotkeyChangeRequested
        /// with the gesture, Ptt.Hotkey set), and ClearHotkey forwards the
        /// null clear request to the same Ptt.
        /// </summary>
        [Fact]
        public void HotkeyCapture_CapturedGestureRequestsPttOnce_ClearRequestsNull()
        {
            var vm = new MainWindowViewModel(null, null, new FakeGlobalHotkeyService());
            Assert.NotNull(vm.HotkeyCapture);
            Assert.NotNull(vm.Ptt);
            var requests = new List<HotkeyGesture?>();
            vm.Ptt.HotkeyChangeRequested += gesture => requests.Add(gesture);
            var gesture = new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

            vm.HotkeyCapture.StartCapture();
            vm.HotkeyCapture.ApplyKey(gesture);

            Assert.Equal(new List<HotkeyGesture?> { gesture }, requests);
            Assert.Equal(gesture, vm.Ptt.Hotkey);
            Assert.False(vm.HotkeyCapture.IsCapturing);

            vm.HotkeyCapture.ClearHotkey();

            Assert.Equal(new List<HotkeyGesture?> { gesture, null }, requests);
            Assert.Null(vm.Ptt.Hotkey);
            Assert.False(vm.HotkeyCapture.IsCapturing);
        }

        /// <summary>
        /// Shape gate for the hotkey-capture composition surface: the
        /// HotkeyCapture property has the exact HotkeyCaptureViewModel
        /// type and is get-only, MainWindowViewModel stays non-disposable,
        /// and construction performs no service access of any kind.
        /// </summary>
        [Fact]
        public void HotkeyCaptureCompositionShape_ExactProperty_NoServiceAccessAtConstruction()
        {
            var main = typeof(MainWindowViewModel);

            var capture = main.GetProperty("HotkeyCapture");
            Assert.NotNull(capture);
            Assert.Equal(typeof(HotkeyCaptureViewModel), capture!.PropertyType);
            Assert.False(capture.CanWrite);

            Assert.False(typeof(IDisposable).IsAssignableFrom(main));

            var fake = new FakeGlobalHotkeyService();
            var vm = new MainWindowViewModel(null, null, fake);

            Assert.NotNull(vm.HotkeyCapture);
            Assert.Equal(0, fake.GetCapabilityCalls);
        }

        /* ------------------------------------------------------------------
        ** Channel-assignment (transmit-target slice)
        ** ---------------------------------------------------------------- */

        private static Codeplug MakeCodeplug()
        {
            return new Codeplug
            {
                Systems = new System.Collections.Generic.List<Codeplug.System>
                {
                    new Codeplug.System { Name = "Repeater 1", Rid = "1000001" },
                },
                Zones = new System.Collections.Generic.List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new System.Collections.Generic.List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "CH 1 DMR", System = "Repeater 1", Tgid = "31001", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 2 P25", System = "Repeater 1", Tgid = "31002", Slot = 2, Mode = "p25" },
                            new Codeplug.Channel { Name = "CH 3", System = "Repeater 1", Tgid = "31003", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 4", System = "Repeater 1", Tgid = "31004", Slot = 2, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 5 Extra", System = "Repeater 1", Tgid = "31005", Slot = 1, Mode = "dmr" },
                        },
                    },
                },
            };
        }

        [Fact]
        public void CodeplugCtor_DefaultZone_SlotsAssignedInOrder()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            Assert.Equal("Zone A", vm.SelectedZone!.Name); // first zone is the default
            Assert.Equal("CH 1 DMR", vm.Channels[0].ChannelName);
            Assert.Equal("CH 2 P25", vm.Channels[1].ChannelName);
            Assert.Equal("CH 3", vm.Channels[2].ChannelName);
            Assert.Equal("CH 4", vm.Channels[3].ChannelName);
        }

        [Fact]
        public void CodeplugCtor_FewerChannelsThanSlots_RemainingUnassigned()
        {
            var codeplug = MakeCodeplug();
            // Keep only the first two channels (remove CH 3, CH 4, CH 5).
            codeplug.Zones[0].Channels.RemoveAt(4);
            codeplug.Zones[0].Channels.RemoveAt(3);
            codeplug.Zones[0].Channels.RemoveAt(2);

            var vm = new MainWindowViewModel(codeplug.Systems, null, null, null, null, codeplug);

            Assert.Equal("CH 1 DMR", vm.Channels[0].ChannelName);
            Assert.Equal("CH 2 P25", vm.Channels[1].ChannelName);
            Assert.Null(vm.Channels[2].ChannelName);
            Assert.Null(vm.Channels[3].ChannelName);
        }

        [Fact]
        public void NullCodeplug_AllSlotsUnassigned()
        {
            var vm = new MainWindowViewModel(null, null, null, null, null, null);

            Assert.All(vm.Channels, c => Assert.Null(c.ChannelName));
        }

        [Fact]
        public void PrimaryChannel_ChannelName_FlowsToPttResolution()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            // WPF-mirrored selection semantics: a plain click selects;
            // a setPrimary click on the selected slot promotes it.
            vm.ProcessChannelClick(1, setPrimary: false);
            vm.ProcessChannelClick(1, setPrimary: true);

            Assert.NotNull(vm.PrimaryChannel);
            Assert.Equal("CH 1 DMR", vm.PrimaryChannel!.ChannelName);
        }
    }
}
