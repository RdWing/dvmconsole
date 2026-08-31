using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopConsoleSessionRuntimeAdapterTests
{
    [Theory]
    [InlineData(nameof(ChannelViewModel.AudioLevel))]
    [InlineData(nameof(ChannelViewModel.AudioPeakLevel))]
    public void MeterPropertiesUseTheBoundedTelemetryPath(string propertyName)
        => Assert.Equal(
            DesktopConsoleSessionRuntimeAdapter.ChannelProjectionChangeKind.Meter,
            DesktopConsoleSessionRuntimeAdapter.ClassifyChannelProperty(propertyName));

    [Theory]
    [InlineData(nameof(ChannelViewModel.AudioFillWidth))]
    [InlineData(nameof(ChannelViewModel.AudioPeakMarkerX))]
    [InlineData(nameof(ChannelViewModel.AudioPeakMarkerBrush))]
    [InlineData(nameof(ChannelViewModel.IsAudioPeakVisible))]
    [InlineData(nameof(ChannelViewModel.CardBackgroundBrush))]
    [InlineData(nameof(ChannelViewModel.CardBorderBrush))]
    [InlineData(nameof(ChannelViewModel.CardTextBrush))]
    public void MeterGeometryAndThemePropertiesDoNotInvalidateControlState(string propertyName)
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
