// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Avalonia.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ZoneViewModelTests
{
    [Fact]
    public void WebStreamPositionExtendsTheSharedZoneCanvas()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        channel.SetWidgetPosition(20, 30);
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live"
        });
        stream.SetWidgetPosition(410, 260);

        var zone = new ZoneViewModel("Dispatch", [channel], [stream]);

        Assert.Same(stream, Assert.Single(zone.WebStreamCards));
        Assert.Equal(410, stream.WidgetX);
        Assert.Equal(260, stream.WidgetY);
        Assert.True(zone.WidgetCanvasWidth >= stream.WidgetX + stream.CardWidth);
        Assert.True(zone.WidgetCanvasHeight >= stream.WidgetY + 122);
        Assert.Equal("Web stream · RX only", stream.CardSubtitle);
    }

    [Fact]
    public void WebStreamOnlyZoneStillHasUsableCanvasBounds()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live"
        });

        var zone = new ZoneViewModel("Dispatch", [], [stream]);

        Assert.Same(stream, Assert.Single(zone.WebStreamCards));
        Assert.Equal(0, stream.WidgetX);
        Assert.Equal(0, stream.WidgetY);
        Assert.True(zone.WidgetCanvasWidth >= stream.CardWidth);
        Assert.True(zone.WidgetCanvasHeight >= 122);
    }

    [Fact]
    public void MovingAWebStreamUpdatesCanvasBoundsIncrementallyAndFinally()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live"
        });
        var zone = new ZoneViewModel("Dispatch", [], [stream]);

        stream.SetWidgetPosition(780, 430);

        Assert.True(zone.WidgetCanvasWidth >= 780 + stream.CardWidth);
        Assert.True(zone.WidgetCanvasHeight >= 430 + 122);

        stream.SetWidgetPosition(20, 30, isFinal: true);

        Assert.True(zone.WidgetCanvasWidth < 780);
        Assert.True(zone.WidgetCanvasHeight < 430);
        Assert.True(zone.WidgetCanvasWidth >= 20 + stream.CardWidth);
    }

    [Fact]
    public async Task WebStreamCardIsNeutralWhenStoppedAndGreenWhileReceiving()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live",
            IdleColor = "#150282"
        });
        int startCount = 0;
        int stopCount = 0;
        stream.Configure(
            candidate =>
            {
                startCount++;
                candidate.SetPlaybackState(true, false, true, false, "Playing");
                return Task.CompletedTask;
            },
            candidate =>
            {
                stopCount++;
                candidate.SetPlaybackState(false, false, false, false, "Off");
                return Task.CompletedTask;
            });

        Assert.Equal(
            Color.Parse("#FFFFFF"),
            Assert.IsType<SolidColorBrush>(stream.CardBackgroundBrush).Color);
        Assert.Equal(
            Color.Parse("#150282"),
            Assert.IsType<SolidColorBrush>(stream.CardBorderBrush).Color);

        stream.SetPlaybackState(true, false, false, false, "Connected; waiting for audio");

        Assert.Equal(
            Color.Parse("#E2F3E8"),
            Assert.IsType<SolidColorBrush>(stream.CardBackgroundBrush).Color);
        Assert.Equal(
            Color.Parse("#4E8060"),
            Assert.IsType<SolidColorBrush>(stream.CardBorderBrush).Color);

        stream.SetPlaybackState(false, false, false, false, "Off");

        await stream.ToggleAsync();

        Assert.Equal(1, startCount);
        Assert.Equal(
            Color.Parse("#008A3A"),
            Assert.IsType<SolidColorBrush>(stream.CardBackgroundBrush).Color);
        Assert.Equal("Stop", stream.ToggleButtonText);

        await stream.ToggleAsync();

        Assert.Equal(1, stopCount);
        Assert.Equal(
            Color.Parse("#FFFFFF"),
            Assert.IsType<SolidColorBrush>(stream.CardBackgroundBrush).Color);
        Assert.Equal("Start", stream.ToggleButtonText);
    }

    [Fact]
    public void AudibleWebStreamActivatesItsZonePresentationIndicator()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live"
        });
        var zone = new ZoneViewModel("Dispatch", [], [stream]);

        Assert.False(zone.IsReceiving);

        stream.SetPlaybackState(
            active: true,
            connecting: false,
            receiving: true,
            failed: false,
            status: "Playing");

        Assert.True(zone.IsReceiving);
        Assert.Equal(1.0, zone.ActivityBarOpacity);

        stream.SetPlaybackState(
            active: false,
            connecting: false,
            receiving: false,
            failed: false,
            status: "Off");

        Assert.False(zone.IsReceiving);
        Assert.True(zone.ActivityBarOpacity < 1.0);
    }

    [Fact]
    public async Task AudibleWebStreamActivatesItsOwningSystemPresentationIndicator()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch feed",
            Url = "https://stream.example.test/live"
        });
        var zone = new ZoneViewModel("Dispatch", [], [stream]);
        await using var system = new SystemViewModel(
            new FneConnectionOptions("System 1", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "System 1",
            "127.0.0.1:62031",
            [],
            [zone]);

        Assert.False(system.IsReceiving);

        stream.SetPlaybackState(
            active: true,
            connecting: false,
            receiving: true,
            failed: false,
            status: "Playing");

        Assert.True(zone.IsReceiving);
        Assert.True(system.IsReceiving);
        Assert.Equal(1.0, system.ActivityBarOpacity);
    }

    [Fact]
    public void ReceiveActivityRequiresEnabledReceivePresentation()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        var zone = new ZoneViewModel("Dispatch", [channel], []);

        Assert.False(zone.IsReceiving);
        Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "LDU1")));
        Assert.False(zone.IsReceiving);

        channel.SetAudioEnabled(true);

        Assert.True(zone.IsReceiving);
        Assert.Equal(1.0, zone.ActivityBarOpacity);

        Assert.True(channel.TryApplyTraffic("System 1", Traffic("TERMINATOR", "TDU")));
        Assert.False(zone.IsReceiving);
        Assert.True(zone.ActivityBarOpacity < 1.0);
    }

    private static FneTrafficFrame Traffic(string callType, string frameType)
        => new(
            FneTrafficProtocol.P25,
            1,
            42,
            100,
            null,
            "GROUP",
            callType,
            frameType,
            1,
            7,
            []);
}
