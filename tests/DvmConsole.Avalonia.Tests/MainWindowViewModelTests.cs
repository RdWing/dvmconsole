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

        // ---- Compile-time shape ----------------------------------------------------

        /// <summary>
        /// Shape gate for the members the XAML shell relies on: sealed types,
        /// exact property types, read-only slots, the PropertyChanged event,
        /// and the SetConnectionState signature. Locked reflectively so a
        /// signature drift fails loudly.
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

            var setState = main.GetMethod(
                nameof(MainWindowViewModel.SetConnectionState),
                new[] { typeof(string), typeof(string), typeof(bool) });
            Assert.NotNull(setState);
            Assert.Equal(typeof(void), setState!.ReturnType);

            var changed = main.GetEvent(nameof(MainWindowViewModel.PropertyChanged));
            Assert.NotNull(changed);
            Assert.Equal(typeof(PropertyChangedEventHandler), changed!.EventHandlerType);

            var slot = typeof(ChannelSlotViewModel);
            Assert.True(slot.IsSealed);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Number))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Label))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.CanWrite);
            Assert.False(slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.CanWrite);
            Assert.Equal(typeof(int), slot.GetProperty(nameof(ChannelSlotViewModel.Number))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Label))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Talkgroup))!.PropertyType);
            Assert.Equal(typeof(string), slot.GetProperty(nameof(ChannelSlotViewModel.Status))!.PropertyType);
            Assert.Equal(typeof(bool), slot.GetProperty(nameof(ChannelSlotViewModel.IsPrimary))!.PropertyType);
        }
    }
}
