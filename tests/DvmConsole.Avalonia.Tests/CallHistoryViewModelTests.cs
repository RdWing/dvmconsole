// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the Call History slice (audit deleg_79328deb
* READY) — DvmConsole.Avalonia.ViewModels.CallHistoryViewModel:
*
*   - Sealed; ctor CallHistoryViewModel(CallHistoryStore store) — null
*     throws ArgumentNullException.
*   - ObservableCollection<CallHistoryEntry> Rows; Refresh() wholesale
*     resyncs Rows from store.Entries (newest-first, in order); no
*     INPC per row (entries immutable), no marshaling (UI thread
*     calls Refresh via Dispatcher).
*/
using System;
using System.Collections.ObjectModel;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="CallHistoryViewModel"/>.
    /// </summary>
    public sealed class CallHistoryViewModelTests
    {
        [Fact]
        public void ApiShape_ExactSurface()
        {
            var type = typeof(CallHistoryViewModel);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(CallHistoryStore) }));
            Assert.Equal(
                typeof(ObservableCollection<CallHistoryEntry>),
                type.GetProperty(nameof(CallHistoryViewModel.Rows))!.PropertyType);
            Assert.NotNull(type.GetMethod(nameof(CallHistoryViewModel.Refresh), Type.EmptyTypes));
        }

        [Fact]
        public void NullStore_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CallHistoryViewModel(null!));
        }

        [Fact]
        public void Refresh_ResyncsRowsFromStore()
        {
            var store = new CallHistoryStore();
            store.AddFrame(new ReceivedCallMetadata("Sys", 100, 200, 1, VoiceMode.Dmr, 1, "k1", false), "CH 1");
            store.AddFrame(new ReceivedCallMetadata("Sys", 101, 201, 1, VoiceMode.P25, 2, "k2", false), "CH 2");

            var vm = new CallHistoryViewModel(store);
            Assert.Empty(vm.Rows); // nothing until Refresh

            vm.Refresh();
            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal("CH 2", vm.Rows[0].ChannelName); // newest first
            Assert.Equal("CH 1", vm.Rows[1].ChannelName);

            // Store changes are reflected only after another Refresh.
            store.AddFrame(new ReceivedCallMetadata("Sys", 102, 202, 1, VoiceMode.Dmr, 3, "k3", false), "CH 3");
            Assert.Equal(2, vm.Rows.Count);
            vm.Refresh();
            Assert.Equal(3, vm.Rows.Count);
        }

        [Fact]
        public void Refresh_EvictionReflected()
        {
            var store = new CallHistoryStore(5);
            var vm = new CallHistoryViewModel(store);
            for (uint i = 1; i <= 7; i++)
            {
                store.AddFrame(new ReceivedCallMetadata("Sys", 100 + i, 200, 1, VoiceMode.Dmr, i, "k" + i, false), "CH " + i);
            }

            vm.Refresh();
            Assert.Equal(5, vm.Rows.Count);
            Assert.Equal("CH 7", vm.Rows[0].ChannelName);
        }
    }
}
