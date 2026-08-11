// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 2.3's card-level receive/TX observables.
    /// State is headless, change-only and safe for the shell to bind.
    /// </summary>
    public sealed class ChannelSlotReceiveStateGateTests
    {
        [Fact]
        public void ReceiveAndTransmitState_UsesWpfDefaultsAndChangeOnlyNotifications()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var changed = new List<string?>();
            slot.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            Assert.False(slot.IsReceiving);
            Assert.False(slot.IsReceivingEncrypted);
            Assert.Equal("Last ID: 0", slot.LastSrcId);
            Assert.False(slot.IsTxEncrypted);
            Assert.False(slot.IsEncryptionSelectable);
            Assert.False(slot.FneConnectionWarningVisible);
            Assert.Equal(string.Empty, slot.FneConnectionWarningToolTip);

            slot.IsReceiving = true;
            slot.IsReceiving = true;
            slot.IsReceivingEncrypted = true;
            slot.IsReceivingEncrypted = true;
            slot.LastSrcId = "Last: Unit 7";
            slot.LastSrcId = "Last: Unit 7";
            slot.IsTxEncrypted = true;
            slot.IsEncryptionSelectable = true;
            slot.FneConnectionWarningVisible = true;
            slot.FneConnectionWarningToolTip = "System 1 disconnected";

            Assert.Equal(
                new[]
                {
                    nameof(ChannelSlotViewModel.IsReceiving),
                    nameof(ChannelSlotViewModel.IsReceivingEncrypted),
                    nameof(ChannelSlotViewModel.LastSrcId),
                    nameof(ChannelSlotViewModel.IsTxEncrypted),
                    nameof(ChannelSlotViewModel.IsEncryptionSelectable),
                    nameof(ChannelSlotViewModel.FneConnectionWarningVisible),
                    nameof(ChannelSlotViewModel.FneConnectionWarningToolTip),
                },
                changed);
        }

        [Fact]
        public void PttEngaged_RemainsTheRenderableTransmitState()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");

            slot.PttEngaged = true;

            Assert.True(slot.PttEngaged);
        }
    }
}
