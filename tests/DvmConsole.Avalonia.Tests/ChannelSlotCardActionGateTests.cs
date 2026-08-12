// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 2.5's headless card indicators and request-only
    /// card actions. Visual buttons and backend behavior are later boundaries.
    /// </summary>
    public sealed class ChannelSlotCardActionGateTests
    {
        [Fact]
        public void IndicatorState_UsesWpfDefaultsAndChangeOnlyNotifications()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var changed = new List<string?>();
            slot.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            Assert.False(slot.IsPatchGroupMember);
            Assert.False(slot.IsPatchGroupActive);
            Assert.False(slot.IsMultiSelectMember);
            Assert.False(slot.PageState);
            Assert.False(slot.HoldState);
            Assert.Equal(string.Empty, slot.GroupIndicatorToolTip);

            slot.IsPatchGroupMember = true;
            slot.IsPatchGroupMember = true;
            slot.IsPatchGroupActive = true;
            slot.IsPatchGroupActive = true;
            slot.IsMultiSelectMember = true;
            slot.IsMultiSelectMember = true;
            slot.PageState = true;
            slot.PageState = true;
            slot.HoldState = true;
            slot.HoldState = true;

            Assert.Equal(
                new[]
                {
                    nameof(ChannelSlotViewModel.IsPatchGroupMember),
                    nameof(ChannelSlotViewModel.GroupIndicatorText),
                    nameof(ChannelSlotViewModel.GroupIndicatorToolTip),
                    nameof(ChannelSlotViewModel.IsPatchGroupActive),
                    nameof(ChannelSlotViewModel.GroupIndicatorText),
                    nameof(ChannelSlotViewModel.GroupIndicatorToolTip),
                    nameof(ChannelSlotViewModel.IsMultiSelectMember),
                    nameof(ChannelSlotViewModel.GroupIndicatorText),
                    nameof(ChannelSlotViewModel.GroupIndicatorToolTip),
                    nameof(ChannelSlotViewModel.PageState),
                    nameof(ChannelSlotViewModel.HoldState),
                },
                changed);
        }

        [Fact]
        public void GroupIndicatorToolTip_UsesMultiSelectThenActivePatchPriority()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");

            Assert.Equal(string.Empty, slot.GroupIndicatorToolTip);

            slot.IsPatchGroupMember = true;
            Assert.Equal("Member of one or more patch groups", slot.GroupIndicatorToolTip);

            slot.IsPatchGroupActive = true;
            Assert.Equal("Member of one or more enabled patch groups", slot.GroupIndicatorToolTip);

            slot.IsMultiSelectMember = true;
            Assert.Equal("Member of the current multi-select group", slot.GroupIndicatorToolTip);
        }

        [Fact]
        public void SelectableEncryptionToolTip_TracksTransmitState()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");

            Assert.Equal(
                "Selectable encryption: clear TX. Click to transmit encrypted.",
                slot.SelectableEncryptionToolTip);

            slot.IsTxEncrypted = true;

            Assert.Equal(
                "Selectable encryption: encrypted TX. Click to transmit clear.",
                slot.SelectableEncryptionToolTip);
        }

        [Fact]
        public void RequestActions_UseWpfGuardsToggleStateAndRaiseSlotPayload()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var pageRequests = new List<ChannelSlotViewModel>();
            var markerRequests = new List<ChannelSlotViewModel>();
            var historyRequests = new List<ChannelSlotViewModel>();
            var encryptionRequests = new List<ChannelSlotViewModel>();
            slot.PageSelectRequested += requested => pageRequests.Add(requested);
            slot.MarkerRequested += requested => markerRequests.Add(requested);
            slot.ChannelHistoryRequested += requested => historyRequests.Add(requested);
            slot.SelectableEncryptionRequested += requested => encryptionRequests.Add(requested);

            slot.RequestPageSelect();
            slot.RequestMarker();
            slot.RequestChannelHistory();
            slot.RequestSelectableEncryption();
            Assert.Empty(pageRequests);
            Assert.Empty(markerRequests);
            Assert.Empty(historyRequests);
            Assert.Empty(encryptionRequests);

            slot.IsSelected = true;
            slot.Reassign(null, null, isRxOnly: true);
            slot.RequestPageSelect();
            slot.RequestMarker();
            Assert.Empty(pageRequests);
            Assert.Empty(markerRequests);

            slot.Reassign(null, null, isRxOnly: false);
            slot.RequestPageSelect();
            slot.RequestPageSelect();
            slot.RequestMarker();
            slot.RequestMarker();
            slot.RequestChannelHistory();
            Assert.Equal(new[] { slot, slot }, pageRequests);
            Assert.Equal(new[] { slot, slot }, markerRequests);
            Assert.Equal(new[] { slot }, historyRequests);
            Assert.False(slot.PageState);
            Assert.False(slot.HoldState);

            slot.IsEncryptionSelectable = true;
            slot.PttEngaged = true;
            slot.RequestSelectableEncryption();
            Assert.Empty(encryptionRequests);

            slot.PttEngaged = false;
            slot.RequestSelectableEncryption();
            Assert.True(slot.IsTxEncrypted);
            slot.RequestSelectableEncryption();
            Assert.False(slot.IsTxEncrypted);
            Assert.Equal(new[] { slot, slot }, encryptionRequests);
        }
    }
}
