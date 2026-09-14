// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Storage;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PreparedLiveSessionTests
{
    [Fact]
    public async Task SavedMediaStateAndReceiveOutputWorkWithoutAViewModel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neo-prepared", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audio = new AudioFactory();
        var dependencies = Dependencies(directory, audio);
        var settings = new UserSettings
        {
            RecordingRootPath = Path.Combine(directory, "recordings"),
            ChannelVolumes = new() { ["Test\u001fDispatch"] = 0.35 },
            ChannelStereoBalances = new() { ["Test\u001fDispatch"] = -0.25 },
            RecordingEnabledChannelKeys = ["Test\u001fDispatch"]
        };
        var configuration = Configuration();
        var state = ConsoleSessionState.Create(configuration);
        var services = new ConsoleSessionServices();
        var runtime = ConsoleOperationalRuntime.Prepare(services, state);
        try
        {
            var plan = FneConsoleRadioSessions.Prepare(state,
                configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                channel => new ChannelConfigurationAccess(channel.Runtime.Definition));
            var radios = await ConsoleRadioSessions.CreateAsync(state, plan, default);
            services.Connection.OwnAsync("test-radios", radios);
            var prepared = new MainWindowViewModel.PreparedLiveSession(runtime, state, settings, dependencies, services);
            MainWindowViewModel.RegisterSessionOwnership(services, prepared);
            prepared.InitializeMedia(radios.Sessions.Values.ToArray(), dependencies, new TransmitKeyPort(null, null, null), false);

            Assert.Null(prepared.ViewModel);
            var channel = Assert.Single(runtime.Channels.Values);
            Assert.Equal(0.35, channel.Operator.Snapshot.Gain);
            Assert.Equal(-0.25, channel.Operator.Snapshot.Balance);
            Assert.True(channel.Operator.Snapshot.RecordingEnabled);
            Assert.NotNull(runtime.Transmit.Microphone);
            Assert.NotNull(runtime.Transmit.Tones);
            Assert.NotNull(runtime.RecordingPlayback);
            Assert.NotNull(runtime.WebPlayback);

            await runtime.Commands.SetReceiveEnabledAsync(channel.Id, true);
            await runtime.Commands.SetChannelGainAsync(channel.Id, 0.6);
            await runtime.Commands.SetChannelBalanceAsync(channel.Id, 0.2);
            Assert.Equal(0.6, settings.ChannelVolumes[channel.SettingsKey]);
            Assert.Equal(0.2, settings.ChannelStereoBalances[channel.SettingsKey]);
            var snapshot = Assert.Single(prepared.Snapshots.Capture().Channels.Values);
            Assert.True(snapshot.ReceiveEnabled);
            Assert.Equal(0.6, snapshot.Gain);
            Assert.Equal(0.2, snapshot.Balance);
            Assert.Same(runtime.RecordingPlaybackState, prepared.PlaybackState);
            string playbackPath = Path.Combine(directory, "playback.wav");
            using (var writer = PcmWavTestFile.Create(playbackPath, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat((short)1200, 1600).ToArray());
            var playing = new TaskCompletionSource<ChannelControlSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            prepared.RecordingPlayback!.PlaybackStateChanged += (_, playback) =>
            {
                if (playback.IsPlaying)
                    playing.TrySetResult(prepared.Snapshots.Capture().Channels[channel.Id]);
            };
            await prepared.RecordingPlayback.StartAsync(playbackPath, identity: new RecordingCallIdentity(
                DateTimeOffset.UtcNow, "Test", "Dispatch", "RX", "P25", 1, 100, 1001, null));
            Assert.True((await playing.Task.WaitAsync(TimeSpan.FromSeconds(10))).RecordingPlayback);
            await prepared.RecordingPlayback.StopAsync();
            Assert.False(prepared.Snapshots.Capture().Channels[channel.Id].RecordingPlayback);
            Assert.True(channel.Operator.Snapshot.AudioEnabled);
            Assert.NotEmpty(audio.Playbacks);
            await services.DisposeAsync();
            Assert.All(audio.Playbacks, playback => Assert.True(playback.Disposed));
            Assert.Throws<ObjectDisposedException>(() => prepared.AudioBackendProvider.CreateBackend());
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingConstructionFailureRetiresResourcesBeforeAnyViewExists()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neo-prepared-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencies = Dependencies(directory, new AudioFactory());
        var state = ConsoleSessionState.Create(Configuration());
        var services = new ConsoleSessionServices();
        var runtime = ConsoleOperationalRuntime.Prepare(services, state);
        var prepared = new MainWindowViewModel.PreparedLiveSession(runtime, state,
            new UserSettings { RecordingRootPath = "invalid\0recording-path" }, dependencies, services);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ConsoleSessionConstruction.CreateAsync(services, _ =>
            {
                MainWindowViewModel.RegisterSessionOwnership(services, prepared);
                prepared.InitializeMedia([], dependencies, new TransmitKeyPort(null, null, null), false);
                return ValueTask.FromResult(prepared);
            }).AsTask());
            Assert.Null(prepared.ViewModel);
            Assert.NotNull(prepared.OwnedAudioBackendProvider);
            Assert.Throws<ObjectDisposedException>(() => prepared.AudioBackendProvider.CreateBackend());
            Assert.Throws<ObjectDisposedException>(() => prepared.TransmitAdmissionGate.Wait(0));
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ConsoleConfiguration Configuration() => new()
    {
        Systems = [new() { Name = "Test", Identity = "Test console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
        Zones = [new() { Name = "Operations", Channels = [new() { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" }] }]
    };

    private static DesktopRuntimeDependencies Dependencies(string directory, IAudioBackendFactory audio)
        => new(new UserSettingsStore(Path.Combine(directory, "settings.json")), () => [],
            (_, _) => throw new InvalidOperationException("Pre-view construction must not start hardware PTT."),
            ImmediateTestUiDispatcher.Instance, new ManagedAssetStore(Path.Combine(directory, "assets")),
            audio, new NativeVocoderFactory(), NetworkDisabledDemo: true);

    private sealed class AudioFactory : IAudioBackendFactory
    {
        public List<Playback> Playbacks { get; } = [];
        public IAudioBackend Create(AudioBackendConfiguration configuration) => new Backend(this);
    }

    private sealed class Backend(AudioFactory owner) : IAudioBackend
    {
        public string Name => "Prepared runtime fixture";
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction) => [new("default", "Fixture", direction, true)];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            var playback = new Playback(format);
            owner.Playbacks.Add(playback);
            return playback;
        }
        public void Dispose() { }
    }

    private sealed class Playback(PcmAudioFormat format) : IAudioPlayback
    {
        public PcmAudioFormat Format => format;
        public bool Disposed { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
