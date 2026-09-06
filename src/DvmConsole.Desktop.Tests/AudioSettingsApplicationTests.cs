// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioSettingsApplicationTests
{
    [Theory]
    [InlineData("dmr", VocoderMode.DmrAmbe)]
    [InlineData("p25", VocoderMode.P25Imbe)]
    [InlineData("nxdn", VocoderMode.NxdnAmbe)]
    public async Task PatchDecoderBypassesRxProcessingAndSurvivesLocalOptionsChange(string protocol, VocoderMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-patch-processing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new ChannelViewModel(new ChannelConfiguration
            {
                Name = "Source",
                System = "Test",
                Tgid = "100",
                Mode = protocol,
                Slot = 1
            });
            var destination = new ChannelViewModel(new ChannelConfiguration
            {
                Name = "Destination",
                System = "Test",
                Tgid = "200",
                Mode = protocol,
                Slot = 1
            });
            var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null) { SourceId = 1001 };
            var system = new SystemViewModel(options, "Test", "127.0.0.1:62031", [source, destination], [], 0,
                new ConnectedRadioSessionFactory(options, source), hasCallPriority: false);
            var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
            store.Save(new UserSettings { RecordingRootPath = Path.Combine(root, "recordings") });
            var vocoders = new ObservedVocoderFactory();
            await using var owner = new MainWindowViewModel("Patch processing", [system], [],
                new MainWindowViewModelOptions(Document: new(store), Host: new(
                    SerialPortProvider: () => [], UiDispatcher: ImmediateTestUiDispatcher.Instance,
                    AudioBackendFactory: new TransmitAudioBackendFactory(), VocoderFactory: vocoders),
                    Features: new(GroupDefinitions: [new GroupConfiguration { Name = "Patch", Type = "patch" }])));

            var group = Assert.Single(owner.PatchGroups);
            foreach (var member in group.Members)
                member.IsMember = true;
            group.IsEnabled = true;
            group.IsOneWay = true;
            group.SelectedSource = group.SourceOptions.Single(member => ReferenceEquals(member.Channel, source));
            Assert.Null(owner.ApplyGroupOperatorStates([group]));
            ObservedVocoderBackend patch = await vocoders.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(mode, patch.Modes);
            foreach (VocoderMode configuredMode in Enum.GetValues<VocoderMode>())
            {
                ReceiveAudioProcessingOptions processing = patch.Options[configuredMode];
                Assert.False(processing.HighPassFilterEnabled);
                Assert.False(processing.PeakingFilterEnabled);
                Assert.False(processing.CompressorEnabled);
            }

            foreach (RxAudioProcessingModeViewModel processing in owner.RxAudioProcessingModes)
            {
                processing.HighPassFilterEnabled = true;
                processing.PeakingFilterEnabled = true;
                processing.CompressorEnabled = true;
            }
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.ApplyRxAudioProcessingOptionsCommand.CanExecuteChanged += (_, _) =>
            {
                if (owner.ApplyRxAudioProcessingOptionsCommand.CanExecute(null))
                    applied.TrySetResult();
            };
            owner.ApplyRxAudioProcessingOptionsCommand.Execute(null);
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("saved and applied", owner.AudioStatusText);
            Assert.Equal(1, vocoders.CreateCount);
            Assert.False(patch.Disposed);
            Assert.All(patch.Options.Values, processing => Assert.False(processing.CompressorEnabled));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ObservedVocoderFactory : IVocoderFactory
    {
        public TaskCompletionSource<ObservedVocoderBackend> Created { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CreateCount { get; private set; }

        public IVocoderBackend Create(IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null)
        {
            CreateCount++;
            return new ObservedVocoderBackend(receiveAudioProcessingOptions!, backend => Created.TrySetResult(backend));
        }
    }

    private sealed class ObservedVocoderBackend(
        IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions> options,
        Action<ObservedVocoderBackend> created) : IVocoderBackend
    {
        public IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions> Options => options;
        public List<VocoderMode> Modes { get; } = [];
        public string Name => "Observed";
        public bool IsAvailable => true;
        public bool Disposed { get; private set; }
        public IVocoderSession CreateSession(VocoderMode mode)
        {
            Modes.Add(mode);
            created(this);
            return new ObservedVocoderSession();
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ObservedVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { }
    }

    [Fact]
    public async Task ProductionSnapshotProjectsRecordingFailureAndSubsequentRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-projection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = new ChannelViewModel(new ChannelConfiguration
            {
                Name = "Dispatch",
                System = "Test",
                Tgid = "100",
                Mode = "analog"
            });
            var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null) { SourceId = 1001 };
            var system = new SystemViewModel(options, "Test", "127.0.0.1:62031", [channel], [], 0,
                new ConnectedRadioSessionFactory(options, channel), hasCallPriority: false);
            var settings = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
            string recordingRoot = Path.Combine(root, "recordings");
            settings.Save(new UserSettings { RecordingRootPath = recordingRoot });
            var audio = new TransmitAudioBackendFactory();
            await using var owner = new MainWindowViewModel("Recording projection", [system], [],
                new MainWindowViewModelOptions(Document: new(settings), Host: new(
                    SerialPortProvider: () => [], UiDispatcher: ImmediateTestUiDispatcher.Instance, AudioBackendFactory: audio)));
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            channel.SetRecordingEnabled(true);
            ChannelId id = new(channel.SessionId);
            Assert.Null(adapter.CaptureSnapshot().Channels[id].RecordingFault);
            string activePath = Path.Combine(recordingRoot, ".active");
            if (Directory.Exists(activePath))
                Directory.Delete(activePath, recursive: true);
            File.WriteAllText(activePath, "Injected obstruction");
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.RecordingStateChanged += OnFault;
            void OnFault(ChannelId changed) { if (changed == id) faulted.TrySetResult(); }
            Assert.True(await owner.StartChannelTransmitAsync(channel));
            audio.LastCapture!.Emit(new short[480]);
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            owner.RecordingStateChanged -= OnFault;
            Assert.NotNull(adapter.CaptureSnapshot().Channels[id].RecordingFault);
            Assert.True(adapter.CaptureSnapshot().Channels[id].TarArmed);
            await owner.StopChannelTransmitAsync(channel);
            File.Delete(activePath);
            // Recover on the next call without requiring the operator to re-arm TAR.
            Assert.True(channel.IsRecordingEnabled);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.RecordingStateChanged += OnRecovered;
            void OnRecovered(ChannelId changed)
            {
                if (changed == id && owner.CaptureChannelRecordingState([channel])[id] is { IsRecording: true, Fault: null })
                    recovered.TrySetResult();
            }
            Assert.True(await owner.StartChannelTransmitAsync(channel));
            audio.LastCapture!.Emit(new short[480]);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            owner.RecordingStateChanged -= OnRecovered;
            Assert.Null(adapter.CaptureSnapshot().Channels[id].RecordingFault);
            await owner.StopChannelTransmitAsync(channel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MicrophoneApplyDisablesDuringTransmitAndReenablesAfterStop()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = new ChannelViewModel(new ChannelConfiguration
            {
                Name = "Dispatch",
                System = "Test",
                Tgid = "100",
                Mode = "analog"
            });
            var connectionOptions = new FneConnectionOptions(
                "Test",
                "Console",
                "127.0.0.1",
                62031,
                1,
                null,
                false,
                null)
            {
                SourceId = 1001
            };
            var system = new SystemViewModel(
                connectionOptions,
                "Test",
                "127.0.0.1:62031",
                [channel],
                [],
                accentIndex: 0,
                new ConnectedRadioSessionFactory(connectionOptions, channel),
                hasCallPriority: false);
            var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
            await using var viewModel = new MainWindowViewModel(
                "Audio command test",
                [system],
                [],
                new MainWindowViewModelOptions(
                    Document: new(store),
                    Host: new(
                        SerialPortProvider: () => [],
                        UiDispatcher: ImmediateTestUiDispatcher.Instance,
                        AudioBackendFactory: new TransmitAudioBackendFactory())));
            int canExecuteChanges = 0;
            viewModel.ApplyAudioInputSettingsCommand.CanExecuteChanged +=
                (_, _) => canExecuteChanges++;

            Assert.True(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            Assert.True(await viewModel.StartChannelTransmitAsync(channel));
            Assert.False(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));

            viewModel.AudioInputGainText = "1.5";
            Assert.False(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));

            await viewModel.StopChannelTransmitAsync(channel);

            Assert.True(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            Assert.True(canExecuteChanges >= 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AudioApplyCommandsRefreshWhenConnectionWorkFinishes()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var viewModel = new MainWindowViewModel(
                "Audio command test",
                [],
                [],
                new MainWindowViewModelOptions(
                    Document: new(new UserSettingsStore(Path.Combine(root, "UserSettings.json"))),
                    Host: new(
                        SerialPortProvider: () => [],
                        UiDispatcher: ImmediateTestUiDispatcher.Instance),
                    Features: new(NetworkDisabledDemo: true)));
            int microphoneChanges = 0;
            int receiveChanges = 0;
            viewModel.ApplyAudioInputSettingsCommand.CanExecuteChanged +=
                (_, _) => microphoneChanges++;
            viewModel.ApplyRxAudioProcessingOptionsCommand.CanExecuteChanged +=
                (_, _) => receiveChanges++;

            viewModel.SetBusy(true);

            Assert.False(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            Assert.False(viewModel.ApplyRxAudioProcessingOptionsCommand.CanExecute(null));

            viewModel.AudioInputGainText = "1.5";
            viewModel.AudioInputAgcEnabled = true;
            viewModel.SetBusy(false);

            Assert.True(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            Assert.True(viewModel.ApplyRxAudioProcessingOptionsCommand.CanExecute(null));
            Assert.Equal(2, microphoneChanges);
            Assert.Equal(2, receiveChanges);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivacyRequestsUseInjectedHostServiceWithoutPlatformTypes()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var permissions = new StubPrivacyPermissionService();
            await using var viewModel = new MainWindowViewModel(
                "Permission test",
                [],
                [],
                new MainWindowViewModelOptions(
                    Document: new(new UserSettingsStore(Path.Combine(root, "UserSettings.json"))),
                    Host: new(
                        SerialPortProvider: () => [],
                        UiDispatcher: ImmediateTestUiDispatcher.Instance,
                        PrivacyPermissionService: permissions),
                    Features: new(NetworkDisabledDemo: true)));

            Assert.True(viewModel.IsMacOsPermissionRequestAvailable);
            viewModel.RequestMacOsKeyboardPermission();
            Assert.Contains("keyboard access requested", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);

            await viewModel.RequestMacOsMicrophonePermissionAsync();
            Assert.Contains("microphone access is denied", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, permissions.MicrophoneRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedRouteChangeRollsBackRuntimeAndDoesNotPersistDraft()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);
        UserSettings original = store.Load();
        var attemptedConfigurations = new List<ApplicationAudioConfiguration>();
        int attempts = 0;

        try
        {
            await using var viewModel = new MainWindowViewModel(
                "Audio settings test",
                [],
                [],
                new MainWindowViewModelOptions(
                    Document: new(store),
                    Host: new(
                        SerialPortProvider: () => [],
                        UiDispatcher: ImmediateTestUiDispatcher.Instance,
                        ReconfigureApplicationAudio: configuration =>
                        {
                            attemptedConfigurations.Add(configuration);
                            if (Interlocked.Increment(ref attempts) == 1)
                                return Task.FromException(new IOException("synthetic route failure"));
                            return Task.CompletedTask;
                        }),
                    Features: new(NetworkDisabledDemo: true)));
            viewModel.AudioInputDeviceIdText = "replacement-input";
            viewModel.AudioOutputDeviceIdText = "replacement-output";

            viewModel.ApplyAudioInputSettingsCommand.Execute(null);
            await WaitUntilAsync(
                () => viewModel.AudioStatusText.Contains(
                    "Unable to apply audio settings",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            Assert.Equal(2, attemptedConfigurations.Count);
            Assert.Equal("replacement-input", attemptedConfigurations[0].InputDeviceId);
            Assert.Equal(original.AudioInputDeviceId, attemptedConfigurations[1].InputDeviceId);
            Assert.True(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            UserSettings persisted = store.Load();
            Assert.Equal(original.AudioInputDeviceId, persisted.AudioInputDeviceId);
            Assert.Equal(original.AudioOutputDeviceId, persisted.AudioOutputDeviceId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private sealed class StubPrivacyPermissionService : IDesktopPrivacyPermissionService
    {
        public bool IsMacOsPermissionRequestAvailable => true;
        public int MicrophoneRequests { get; private set; }

        public KeyboardPermissionState RequestKeyboardAccess()
            => KeyboardPermissionState.Requested;

        public ValueTask<MicrophonePermissionState> GetStateAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MicrophonePermissionState.Denied);

        public ValueTask<MicrophonePermissionState> RequestAsync(
            CancellationToken cancellationToken = default)
        {
            MicrophoneRequests++;
            return ValueTask.FromResult(MicrophonePermissionState.Denied);
        }
    }

    private sealed class TransmitAudioBackendFactory : IAudioBackendFactory
    {
        public ImmediateCapture? LastCapture { get; set; }
        public IAudioBackend Create(AudioBackendConfiguration configuration)
            => new TransmitAudioBackend(this);
    }

    private sealed class TransmitAudioBackend(TransmitAudioBackendFactory owner) : IAudioBackend
    {
        public string Name => "test";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new AudioDeviceInfo("default", "Test", direction, true, false)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => owner.LastCapture = new ImmediateCapture();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;

        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }

        public void Emit(short[] samples) => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(new short[160]));
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectedRadioSessionFactory(
        FneConnectionOptions options,
        ChannelViewModel channel) : IFneRadioSessionFactory
    {
        public RadioSystemDescriptor Descriptor { get; } = new(
            SystemId.FromName(options.Name),
            options.Name,
            "fne",
            new Dictionary<string, string>());

        public IFneRadioSession Create() => new ConnectedRadioSession(options, channel);
    }

    private sealed class ConnectedRadioSession(
        FneConnectionOptions options,
        ChannelViewModel channel) : IFneRadioSession
    {
        public SystemId SystemId { get; } = SystemId.FromName(options.Name);
        public string Name => options.Name;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors
            => [channel.ToTransmitDescriptor()];
        public IReadOnlyCollection<ChannelId> ChannelIds
            => [new ChannelId(channel.SessionId)];
        public bool IsConnected => true;
        public bool IsConnectionActive => true;
        public uint? SourceId => options.SourceId;
        public string Identity => options.Identity;
        public FneConnectionStatus Status { get; } = new(
            options.Name,
            FneConnectionState.Connected,
            "Connected for test",
            DateTimeOffset.UtcNow);

        public event EventHandler<RadioTrafficRecord>? TrafficReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<FneConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<FneLogEntry>? LogReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<FneKeyResponse>? KeyResponseReceived
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Abort() { }

        public void SetVerboseLogging(bool enabled)
        {
        }

        public FneTalkgroupAvailability GetTalkgroupAvailability(
            FneTrafficProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => FneTalkgroupAvailability.Available;

        public uint CreateStreamId() => 42;

        public void SendTraffic(
            RadioMediaProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint streamId)
        {
        }

        public void SendTraffic(
            FneTrafficProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint streamId)
        {
        }

        public TargetAuthorityState GetTargetAuthority(
            RadioMediaProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => TargetAuthorityState.Available;

        public void RequestP25Key(byte algorithmId, ushort keyId)
        {
        }

        public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
