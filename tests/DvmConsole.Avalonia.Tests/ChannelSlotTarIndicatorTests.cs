// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the dashboard TAR indicator state. Slot state is
    /// headless and change-only; shell wiring and recorder lifecycle are later.
    /// </summary>
    public sealed class ChannelSlotTarIndicatorTests
    {
        [Fact]
        public void TarRecordingIndicator_IsChangeOnlyAndUsesWpfTooltipText()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var changed = new List<string?>();
            slot.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

            Assert.False(slot.TarRecordingEnabled);
            Assert.Equal("TAR recording disabled for this channel", slot.TarRecordingIndicatorToolTip);

            slot.TarRecordingEnabled = true;
            slot.TarRecordingEnabled = true;

            Assert.True(slot.TarRecordingEnabled);
            Assert.Equal("TAR recording enabled for this channel", slot.TarRecordingIndicatorToolTip);
            Assert.Equal(new[] { nameof(ChannelSlotViewModel.TarRecordingEnabled) }, changed);

            slot.TarRecordingEnabled = false;
            Assert.Equal(2, changed.Count);
            Assert.Equal("TAR recording disabled for this channel", slot.TarRecordingIndicatorToolTip);
        }
    }
}
