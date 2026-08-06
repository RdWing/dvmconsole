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
* The tests are fully headless and pure managed: no Avalonia.Headless
* package, window, display, native call, file, or secret is involved.
*
* GREEN contract gate: the production view-models implement this exact
* contract without Avalonia, protocol, audio, or network coupling.
*/
#nullable enable
using System.ComponentModel;
using DvmConsole.Avalonia.ViewModels;
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
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.CanWrite);
            Assert.True(slot.GetProperty(nameof(ChannelSlotViewModel.IsSelected))!.CanWrite);
            Assert.True(slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.CanWrite);
            Assert.Equal(typeof(int), slot.GetProperty(nameof(ChannelSlotViewModel.Number))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Label))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.PropertyType);
            Assert.Equal(typeof(bool), slot.GetProperty(nameof(ChannelSlotViewModel.IsSelected))!.PropertyType);
            Assert.Equal(typeof(bool), slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.PropertyType);
        }
    }
}
