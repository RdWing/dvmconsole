// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract pin that the existing PTT slice remains the owner of
    /// momentary/toggle engagement and press-time all-channel snapshots.
    /// </summary>
    public sealed class MainWindowAudioPttApplicationContractTests
    {
        [Fact]
        public void PttCapability_AlreadyExposesTheGate33EngagementSurface()
        {
            var type = typeof(PttCapabilityViewModel);

            Assert.NotNull(type.GetProperty(nameof(PttCapabilityViewModel.ToggleMode)));
            Assert.NotNull(type.GetProperty(nameof(PttCapabilityViewModel.AllChannels)));
            Assert.NotNull(type.GetProperty(nameof(PttCapabilityViewModel.EngagedTargets)));
            Assert.NotNull(type.GetEvent(nameof(PttCapabilityViewModel.PttStateRequested)));
        }
    }
}
