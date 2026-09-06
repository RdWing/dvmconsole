// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class WebStreamOperatorControllerTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    public void CardVolumeUsesChannelGainMapping(double position, double gain)
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch",
            Url = "https://example.invalid/live"
        });
        stream.VolumeSliderValue = position;
        Assert.Equal(gain, stream.Volume);
        stream.Volume = 0.5;
        stream.Volume = gain;
        Assert.Equal(position, stream.VolumeSliderValue);
    }

    [Fact]
    public async Task InitializeRestoresPresentationAndDemoToggleNeverCreatesNetworkAudio()
    {
        var settings = new UserSettings
        {
            WebStreamVolumes = new Dictionary<string, double> { ["Dispatch"] = 0.75 },
            WebStreamOutputDeviceIds = new Dictionary<string, string> { ["Dispatch"] = "radio" }
        };
        var session = new TestSession { NetworkAccessDisabled = true };
        var playback = new WebStreamPlaybackCoordinator(
            () => throw new InvalidOperationException("audio must not be created"),
            () => "default");
        await using var controller = new WebStreamOperatorController(
            settings,
            "/managed/example.yml",
            playback,
            session);
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch",
            Url = "https://example.invalid/live"
        });

        controller.Initialize([stream], []);
        await stream.ToggleAsync();

        Assert.Same(stream, Assert.Single(controller.Streams));
        Assert.Equal(0.75, stream.Volume);
        Assert.Equal("radio", stream.OutputDeviceIdText);
        Assert.False(stream.IsActive);
        Assert.Equal("Demo offline", stream.StatusText);
        Assert.Contains("network access is disabled", session.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VolumeAndOutputRoutePersistThroughTheControllerAndDetachOnDisposal()
    {
        var settings = new UserSettings();
        var session = new TestSession();
        var playback = new WebStreamPlaybackCoordinator(
            () => throw new InvalidOperationException("audio must not be created"),
            () => "default");
        var controller = new WebStreamOperatorController(
            settings,
            "/managed/example.yml",
            playback,
            session);
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch",
            Url = "https://example.invalid/live"
        });
        controller.Initialize([stream], []);

        stream.Volume = 1.5;
        stream.OutputDeviceIdText = "speakers";
        Assert.True(controller.SaveOutputDevice(stream));

        Assert.Equal(1.5, settings.WebStreamVolumes[stream.Name]);
        Assert.Equal("speakers", settings.WebStreamOutputDeviceIds[stream.Name]);
        Assert.Equal(2, session.PersistCount);

        await controller.DisposeAsync();
        stream.Volume = 2.0;

        Assert.Equal(2, session.PersistCount);
    }

    [Fact]
    public async Task RestorationAndDisposalAreSharedAndShutdownCancelsAnOpeningStream()
    {
        const string configurationIdentity = "/managed/example.yml";
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch",
            Url = "https://example.invalid/live"
        });
        var settings = new UserSettings { RestoreSelectedChannelsOnStartup = true };
        settings.SelectedWebStreams.Add(
            WebStreamSelectionIdentity.Create(configurationIdentity, stream));
        var session = new TestSession();
        var backend = new RestoreAudioBackend();
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new WebStreamPlaybackCoordinator(
            () => backend,
            () => "default",
            async (_, cancellationToken) =>
            {
                openStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Stream.Null;
            });
        var controller = new WebStreamOperatorController(
            settings,
            configurationIdentity,
            playback,
            session);
        controller.Initialize([stream], []);

        Task firstRestore = controller.RestoreSelectedForSessionAsync();
        Task secondRestore = controller.RestoreSelectedForSessionAsync();
        Assert.Same(firstRestore, secondRestore);
        await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Task firstDispose = controller.DisposeAsync().AsTask();
        Task secondDispose = controller.DisposeAsync().AsTask();

        Assert.Same(firstDispose, secondDispose);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRestore);
        Assert.True(backend.IsDisposed);
        Assert.False(stream.IsActive);
    }

    private sealed class TestSession : IWebStreamOperatorSession
    {
        public bool NetworkAccessDisabled { get; set; }
        public int PersistCount { get; private set; }
        public string AudioStatus { get; private set; } = string.Empty;

        public ValueTask InvokeUiAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }

        public void PersistSettings() => PersistCount++;
        public void PublishAudioStatus(string text) => AudioStatus = text;
    }

    private sealed class RestoreAudioBackend : IAudioBackend
    {
        public string Name => "restore-test";
        public bool IsDisposed { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("default", "Default", direction, true)]
                : [];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
