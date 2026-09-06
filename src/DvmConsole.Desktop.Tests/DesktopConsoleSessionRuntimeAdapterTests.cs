// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using System.Collections.Immutable;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopConsoleSessionRuntimeAdapterTests
{
    [Fact]
    public void IncrementalProjectionRebuildsOnlyDirtyEntries()
    {
        ImmutableDictionary<int, object> previous = new Dictionary<int, object>
        {
            [1] = new object(),
            [2] = new object(),
            [3] = new object()
        }.ToImmutableDictionary();
        int projectionCount = 0;

        ImmutableDictionary<int, object> current =
            DesktopConsoleSnapshotProjector.UpdateProjection(
                previous,
                [2],
                _ =>
                {
                    projectionCount++;
                    return new object();
                });

        Assert.Equal(1, projectionCount);
        Assert.Same(previous[1], current[1]);
        Assert.NotSame(previous[2], current[2]);
        Assert.Same(previous[3], current[3]);
    }

    [Fact]
    public void CombinedMeterPropertyUsesTheBoundedTelemetryPath()
        => Assert.Equal(
            DesktopConsoleSessionRuntimeAdapter.ChannelProjectionChangeKind.Meter,
            DesktopConsoleSessionRuntimeAdapter.ClassifyChannelProperty(
                nameof(ChannelViewModel.AudioMeter)));

    [Theory]
    [InlineData(nameof(ChannelViewModel.AudioLevel))]
    [InlineData(nameof(ChannelViewModel.AudioPeakLevel))]
    [InlineData(nameof(ChannelViewModel.CardBackgroundBrush))]
    [InlineData(nameof(ChannelViewModel.CardBorderBrush))]
    [InlineData(nameof(ChannelViewModel.CardTextBrush))]
    public void LegacyMeterAndThemePropertiesDoNotInvalidateControlState(string propertyName)
        => Assert.Equal(
            DesktopConsoleSessionRuntimeAdapter.ChannelProjectionChangeKind.None,
            DesktopConsoleSessionRuntimeAdapter.ClassifyChannelProperty(propertyName));

    [Theory]
    [InlineData(nameof(ChannelViewModel.State))]
    [InlineData(nameof(ChannelViewModel.IsAudioEnabled))]
    [InlineData(nameof(ChannelViewModel.IsTransmitSelected))]
    [InlineData(nameof(ChannelViewModel.Volume))]
    [InlineData(nameof(ChannelViewModel.TalkgroupAvailability))]
    public void ProjectedControlPropertiesInvalidateTheSnapshot(string propertyName)
        => Assert.Equal(
            DesktopConsoleSessionRuntimeAdapter.ChannelProjectionChangeKind.Control,
            DesktopConsoleSessionRuntimeAdapter.ClassifyChannelProperty(propertyName));
}
