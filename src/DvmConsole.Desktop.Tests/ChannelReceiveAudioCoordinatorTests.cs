// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelReceiveAudioCoordinatorTests
{
    [Fact]
    public async Task MonitorSharesReceiveOutputAndRetainsItAfterLastReceiveChannelStops()
    {
        var backends = new List<FakeAudioBackend>();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() =>
        { var backend = new FakeAudioBackend(); backends.Add(backend); return backend; }, () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Receive", System = "System 1", Tgid = "100", Mode = "analog" });
        await coordinator.StartAsync(channel);
        await using var monitor = await coordinator.OpenMonitorOutputAsync();
        Assert.Equal(2, backends.Count);
        Assert.True(backends[1].IsDisposed);
        Assert.False(backends[0].IsDisposed);
        await coordinator.StopAsync(channel);
        Assert.False(backends[0].IsDisposed);
        await monitor.WriteAsync(Enumerable.Repeat((short)1200, 320).ToArray());
        await monitor.DrainAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(backends[0].Playback.Frames, frame => frame.Any(sample => sample != 0));
        await monitor.DisposeAsync();
        Assert.True(backends[0].IsDisposed);
    }

    [Fact]
    public async Task RouteRecoveryRetiresMonitorAndLateLeaseDisposalPreservesReplacement()
    {
        var backends = new List<FakeAudioBackend>();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() =>
        { var backend = new FakeAudioBackend(); backends.Add(backend); return backend; }, () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Receive", System = "System 1", Tgid = "100", Mode = "analog" });
        await coordinator.StartAsync(channel);
        await using var monitor = await coordinator.OpenMonitorOutputAsync();
        var result = await coordinator.RecoverSelectedAsync([channel.SessionState.Id]);
        Assert.Single(result.Restarted);
        Assert.True(backends[0].IsDisposed);
        Assert.False(backends[^1].IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.WriteAsync(new short[160]).AsTask());
        await monitor.DisposeAsync();
        Assert.False(backends[^1].IsDisposed);
    }

    [Fact]
    public async Task FullStopRetiresMonitorOnlyRouteAndAllowsFreshOutput()
    {
        var backends = new List<FakeAudioBackend>();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() =>
        { var backend = new FakeAudioBackend(); backends.Add(backend); return backend; }, () => new FakeVocoderBackend());
        await using var old = await coordinator.OpenMonitorOutputAsync();
        await coordinator.StopAsync();
        Assert.True(backends[0].IsDisposed);
        await using var current = await coordinator.OpenMonitorOutputAsync();
        await old.DisposeAsync();
        Assert.False(backends[^1].IsDisposed);
        await coordinator.DisposeAsync();
        Assert.True(backends[^1].IsDisposed);
    }

    [Fact]
    public async Task SharedReceiveSessionRoutesDmrIngressThroughDecoderMixerAndMeter()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "System 1", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new ZoneConfiguration { Name = "Test", Channels = [new ChannelConfiguration
                { Name = "Receive", System = "System 1", Tgid = "100", Mode = "dmr", Slot = 1 }] }]
        };
        var radio = new IngressRadio();
        var backend = new FakeAudioBackend();
        var codec = new DeferredProcessingVocoder();
        var meter = new TaskCompletionSource<ChannelMeterSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = await ConsoleReceiveSession.CreateAsync(configuration, (state, _) =>
        {
            radio.Channels = state.Topology.Channels.Select(channel => channel.Id).ToArray();
            var descriptor = new RadioSystemDescriptor(radio.SystemId, radio.Name, "FNE", new Dictionary<string, string>());
            var host = new ConsoleHostServices(radio, new SessionAudioFactory(backend), new SessionVocoderFactory(codec),
                null!, null!, null!, null!, SystemClock.Instance,
                new BackgroundApplicationScheduler(exception => meter.TrySetException(exception)), SystemApplicationDelay.Instance, null!, []);
            return new(host, [descriptor], FneReceiveFrameNormalization.Instance);
        });
        var logs = new List<ConsoleLogEvent>();
        runtime.LogPublished += (_, _) => throw new InvalidOperationException("Broken observer");
        runtime.LogPublished += (_, entry) => logs.Add(entry);
        radio.EmitLog(new(DateTimeOffset.UtcNow, radio.Name, DebugLogSeverity.Debug, "Transport diagnostic"));
        Assert.Equal(ConsoleLogLevel.Debug, Assert.Single(logs).Level);
        Assert.Equal("Transport diagnostic", logs[0].Message);
        runtime.MeterSampled += (_, sample) => { if (sample.Peak > 0) meter.TrySetResult(sample); };
        ChannelId channel = runtime.CaptureTopology().Channels.Single().Id;
        await runtime.SetReceiveEnabledAsync(channel, true);
        for (ushort sequence = 1; sequence <= 6; sequence++) radio.Emit(CreateTraffic(100, 0, sequence));
        short[] output = await backend.Playback.NonSilent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ChannelMeterSample observed = await meter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(output, sample => sample != 0);
        Assert.True(codec.ProcessCalls > 0);
        Assert.Equal(channel, observed.ChannelId);
        Assert.Equal("2", runtime.CaptureSnapshot().Channels[channel].LastCaller);
        Assert.Single(runtime.History);
        await runtime.DisposeAsync();
        Assert.True(backend.IsDisposed);
        Assert.True(backend.Playback.IsDisposed);
        Assert.Equal(1, radio.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedReceiveTarFinalizesWithOrWithoutLocalListening(bool listen)
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-receive-tar-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new OpusRecordingStore(root, null, 0);
            var configuration = new ConsoleConfiguration
            {
                Systems = [new SystemConfiguration { Name = "System 1", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
                Zones = [new ZoneConfiguration { Name = "Test", Channels = [new ChannelConfiguration
                    { Name = "Receive", System = "System 1", Tgid = "100", Mode = "dmr", Slot = 1 }] }]
            };
            var radio = new IngressRadio();
            var backend = new FakeAudioBackend();
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalized = new TaskCompletionSource<RecordingFinalizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.RecordingFinalized += (_, result) => finalized.TrySetResult(result);
            await using var runtime = await ConsoleReceiveSession.CreateAsync(configuration, (state, ownership) =>
            {
                radio.Channels = state.Topology.Channels.Select(channel => channel.Id).ToArray();
                var capture = ownership.Recording.OwnAsync("test-capture", new CallRecordingManager(store));
                capture.RecordingStateChanged += id =>
                {
                    if (((IReceiveRecordingSession)capture).CaptureState(id).IsRecording) captured.TrySetResult();
                };
                var descriptor = new RadioSystemDescriptor(radio.SystemId, radio.Name, "FNE", new Dictionary<string, string>());
                var host = new ConsoleHostServices(radio, new SessionAudioFactory(backend), new SessionVocoderFactory(new DeferredProcessingVocoder(useRecordingSignal: true)),
                    null!, null!, store, null!, SystemClock.Instance,
                    new BackgroundApplicationScheduler(exception => captured.TrySetException(exception)), SystemApplicationDelay.Instance, null!, []);
                return new(host, [descriptor], FneReceiveFrameNormalization.Instance, Recordings: capture);
            });
            ChannelId channel = runtime.CaptureTopology().Channels.Single().Id;
            if (listen) await runtime.SetReceiveEnabledAsync(channel, true);
            await runtime.SetRecordingEnabledAsync(channel, true);
            for (ushort sequence = 1; sequence <= 6; sequence++) radio.Emit(CreateTraffic(100, 0, sequence));
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(runtime.CaptureSnapshot().Channels[channel].TarArmed);
            if (listen) await backend.Playback.NonSilent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            else Assert.False(backend.Playback.NonSilent.Task.IsCompleted);
            await runtime.QuiesceAsync(CancellationToken.None);
            RecordingFinalizationResult result = await finalized.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.Error);
            Assert.True(result.IsPlayable, result.Diagnostic);
            Assert.Equal("Receive", result.Metadata!.ChannelName);
            Assert.False(runtime.CaptureSnapshot().Channels[channel].Recording);
            var archive = new DvmConsole.Storage.OpusRecordingArchive(store);
            var entry = Assert.Single(await archive.LoadAsync());
            Assert.Equal("Receive", entry.ChannelName);
            Assert.True(entry.IsPlayable);
            Assert.Equal(Path.GetFileName(result.Metadata.FilePath), entry.FileName);
            using var exported = new MemoryStream();
            await archive.ExportAsync(entry.Id, exported);
            Assert.Equal(File.ReadAllBytes(result.Metadata.FilePath), exported.ToArray());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.DeleteAsync(entry.Id, new CancellationToken(true)));
            Assert.Single(await archive.LoadAsync());
            Assert.True(await archive.DeleteAsync(entry.Id));
            Assert.Empty(await archive.LoadAsync());
            Assert.False(await archive.DeleteAsync(entry.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class SessionAudioFactory(IAudioBackend backend) : IAudioBackendFactory
    { public IAudioBackend Create(AudioBackendConfiguration configuration) => backend; }
    private sealed class SessionVocoderFactory(IVocoderBackend backend) : IVocoderFactory
    { public IVocoderBackend Create(IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null) => backend; }
    private sealed class IngressRadio : IRadioSession, IRadioSessionFactory, IRadioLogSource
    {
        public ChannelId[] Channels = [];
        public int Disposals;
        public event EventHandler<DebugLogEntry>? LogPublished;
        public void EmitLog(DebugLogEntry entry) => LogPublished?.Invoke(this, entry);
        public SystemId SystemId => SystemId.FromName(Name);
        public string Name => "System 1";
        public bool IsConnected => false;
        public bool IsConnectionActive => false;
        public uint? SourceId => 1001;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => Channels;
        public event EventHandler<RadioTrafficRecord>? TrafficReceived;
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged { add { } remove { } }
        public void Emit(IRadioMediaFrame frame) => TrafficReceived?.Invoke(this, new(SystemId, Channels, frame, DateTimeOffset.UtcNow));
        public ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor descriptor, CancellationToken cancellationToken = default) => ValueTask.FromResult<IRadioSession>(this);
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot) => TargetAuthorityState.Available;
        public uint CreateStreamId() => 1;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId) { }
    }

    [Fact]
    public async Task RecordingAndListeningShareOneRawDecoderWithPresentationOnlyProcessing()
    {
        var vocoder = new DeferredProcessingVocoder();
        int observed = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => new FakeAudioBackend(), () => vocoder,
            samplesObserver: (_, _, _, samples) =>
            {
                Assert.All(samples.ToArray(), sample => Assert.Equal(100, sample));
                observed += samples.Length;
            });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "TAR",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));
        Assert.True(observed > 0);
        Assert.True(vocoder.ProcessCalls > 0);
        int processingCalls = vocoder.ProcessCalls;
        int firstObserved = observed;
        await coordinator.SetLivePlaybackEnabledAsync(channel, false);
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, packetSequence: 2));
        Assert.True(observed > firstObserved);
        Assert.Equal(processingCalls, vocoder.ProcessCalls);
        Assert.Equal(1, vocoder.SessionsCreated);
    }

    private sealed class DeferredProcessingVocoder(bool useRecordingSignal = false) : IVocoderBackend, IVocoderSession, IReceiveAudioProcessingSession
    {
        private bool deferred;
        public int SessionsCreated { get; private set; }
        public int ProcessCalls { get; private set; }
        public string Name => "Deferred test";
        public bool IsAvailable => true;
        public bool HasReceiveAudioProcessing => true;
        public IVocoderSession CreateSession(VocoderMode mode) { SessionsCreated++; return this; }
        public void DeferReceiveAudioProcessing() => deferred = true;
        public void ProcessReceiveAudio(Span<short> samples) { ProcessCalls++; samples.Fill(200); }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            if (useRecordingSignal)
                for (int index = 0; index < samples.Length; index++) samples[index] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * index / 8000));
            else samples.Fill((short)(deferred ? 100 : 200));
            return 0;
        }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;
        public void Dispose() { }
    }

    [Fact]
    public async Task ReusesWorkerCancellationAndSkipsExpiryScansOnWarmedFrames()
    {
        var backend = new FakeAudioBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);
        FneTrafficFrame header = CreateDmrVoiceLcHeader(100, slot: 1);
        using var workerLifetime = new CancellationTokenSource();

        for (int index = 0; index < 10_000; index++)
            await coordinator.ProcessAsync(channel, header, workerLifetime.Token);

        ReceiveAudioHotPathDiagnostics diagnostics = coordinator.GetHotPathDiagnostics(channel);
        Assert.Equal(1, diagnostics.LinkedCancellationSourceCreations);
        Assert.Equal(0, diagnostics.CompletedStreamExpiryScans);
    }

    [Fact]
    public async Task ReportsSessionGateAndProcessingTimingWithoutChangingProcessContract()
    {
        var backend = new FakeAudioBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        await coordinator.StartAsync(channel);

        ReceiveAudioProcessTiming timing = await coordinator.ProcessWithTimingAsync(
            channel,
            CreateAnalogTraffic(100));

        Assert.True(timing.Measured);
        Assert.Equal(false, timing.EncryptedSessionProcessing);
        Assert.True(timing.SessionGateDelay >= TimeSpan.Zero);
        Assert.True(timing.SessionProcessingDuration >= TimeSpan.Zero);
        Assert.True(timing.FramesDecoded >= 0);
    }

    [Fact]
    public async Task RetainsSignaledEncryptionTimingAcrossLaterVoiceFrames()
    {
        var backend = new FakeAudioBackend();
        var keyRing = new P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            keyRing);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        }, keyRing);
        await coordinator.StartAsync(channel);
        byte[] encryptedPayload = P25DfsiFrameCodec.CreateEncryptedLdu1Payload(
            sourceId: 2,
            destinationId: 100,
            encryptedImbe: new byte[P25DfsiFrameCodec.ImbeBytes],
            metadata: new P25DfsiFrameCodec.P25EncryptionMetadata(
                P25Defines.P25_ALGO_AES,
                KeyId: 0x50,
                MessageIndicator: new byte[9]));
        var encrypted = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 1,
            streamId: 99,
            payload: encryptedPayload);

        ReceiveAudioProcessTiming signaled = await coordinator.ProcessWithTimingAsync(
            channel,
            encrypted);
        ReceiveAudioProcessTiming retained = await coordinator.ProcessWithTimingAsync(
            channel,
            CreateP25Traffic(100, packetSequence: 2));

        Assert.Equal(true, signaled.EncryptedSessionProcessing);
        Assert.Equal(true, retained.EncryptedSessionProcessing);
    }

    [Fact]
    public async Task ProcessesDifferentChannelsConcurrently()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new BlockingFirstVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var first = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 1",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var second = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 2",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 2
        });
        await coordinator.StartAsync(first);
        await coordinator.StartAsync(second);

        Task<int> firstWork = Task.Run(() => coordinator.ProcessAsync(first, CreateTraffic(100, 0)));
        await vocoder.FirstDecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<int> secondWork = Task.Run(() => coordinator.ProcessAsync(second, CreateTraffic(101, 1)));

        Task completed = await Task.WhenAny(secondWork, Task.Delay(TimeSpan.FromMilliseconds(500)));
        vocoder.ReleaseFirstDecode.TrySetResult();
        await firstWork;

        Assert.Same(secondWork, completed);
        Assert.Equal(0, await secondWork);
    }

    [Fact]
    public async Task DecodedSamplesRetainTheProcessedFrameIdentityWhenChannelStateChanges()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new BlockingFirstVocoderBackend();
        var observed = new TaskCompletionSource<(uint StreamId, uint SourceId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            samplesObserver: (_, streamId, sourceId, _) => observed.TrySetResult((streamId, sourceId)));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        FneTrafficFrame first = CreateTraffic(100, 0, streamId: 41);
        FneTrafficFrame second = CreateTraffic(100, 0, packetSequence: 2, streamId: 42);
        channel.ApplyTraffic("System 1", first, now);
        await coordinator.StartAsync(channel);

        Task<int> processing = Task.Run(() => coordinator.ProcessAsync(channel, first));
        await vocoder.FirstDecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        channel.ApplyTraffic("System 1", second, now.AddMilliseconds(100));
        vocoder.ReleaseFirstDecode.TrySetResult();

        await processing;
        (uint streamId, uint sourceId) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((uint)41, streamId);
        Assert.Equal((uint)2, sourceId);
    }

    [Fact]
    public async Task ColdTransitionDiscardsOnlyLivePlaybackWhileSamplesRemainObservable()
    {
        var backend = new FakeAudioBackend();
        int observedSamples = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, samples) => observedSamples += samples.Length);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        await coordinator.StartAsync(channel);

        long discardedBefore = coordinator.SetLivePlaybackDiscarded(discarded: true);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100));
        await Task.Delay(40);

        Assert.Equal(AnalogVoicePacketCodec.SamplesPerPacket, observedSamples);
        Assert.Empty(backend.Playback.Frames);
        Assert.True(
            coordinator.GetPlaybackDiagnostics(channel)!.TransitionDiscardedSamples >
            discardedBefore);

        coordinator.SetLivePlaybackDiscarded(discarded: false);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100, packetSequence: 2));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);
    }

    [Fact]
    public async Task DesktopRollbackRestoresPlaybackWithoutOverridingOperatorMute()
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-rollback-audio", Guid.NewGuid().ToString("N"));
        var backend = new FakeAudioBackend();
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Dispatch", System = "System 1", Tgid = "100", Mode = "analog" });
        var system = new SystemViewModel(new FneConnectionOptions(
            "System 1", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "System 1", "local", [channel]);
        var store = new DvmConsole.Core.Settings.UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var owner = new MainWindowViewModel("Ready", [system], [],
            new MainWindowViewModelOptions(Document: new(store),
                Host: new(SerialPortProvider: () => [], UiDispatcher: ImmediateTestUiDispatcher.Instance,
                    AudioBackendFactory: new SessionAudioFactory(backend),
                    VocoderFactory: new SessionVocoderFactory(new FakeVocoderBackend())),
                Features: new(NetworkDisabledDemo: true)));
        try
        {
            var audio = owner.OperationalRuntime.Receive.Audio;
            await audio.StartAsync(new(channel.Id, channel.SessionState.Runtime.Definition));
            owner.OutputMuted = true;
            owner.SuppressSessionInputForTransition();
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            ((IConsoleSessionReactivation)adapter).ReactivateAfterFailedReplacement();
            Assert.True(owner.OutputMuted);
            await audio.ProcessAsync(channel.Id, CreateAnalogTraffic(100));
            Assert.True(audio.GetPlaybackDiagnostics(channel.Id)!.TransitionDiscardedSamples > 0);
            Assert.Empty(backend.Playback.Frames);

            owner.OutputMuted = false;
            await audio.ProcessAsync(channel.Id, CreateAnalogTraffic(100, packetSequence: 2));
            await WaitForAsync(() => backend.Playback.Frames.Count > 0);

            owner.ApplyFinalShutdownSafetyFence();
            ((IConsoleSessionReactivation)adapter).ReactivateAfterFailedReplacement();
            Assert.True(owner.IsSessionInputSuppressed);
            Assert.False(await owner.OperationalRuntime.Transmit.BeginChannelAsync(channel.Id));
        }
        finally
        {
            await owner.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OperatorMuteKeepsTarObservationActiveAndIsIndependentFromTransitionGating()
    {
        var backend = new FakeAudioBackend();
        int observedSamples = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, samples) => observedSamples += samples.Length);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        await coordinator.StartAsync(channel);

        coordinator.SetOutputMuted(muted: true);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100));
        coordinator.SetLivePlaybackDiscarded(discarded: true);
        coordinator.SetOutputMuted(muted: false);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100, packetSequence: 2));
        await Task.Delay(40);

        Assert.Equal(AnalogVoicePacketCodec.SamplesPerPacket * 2, observedSamples);
        Assert.Empty(backend.Playback.Frames);

        coordinator.SetLivePlaybackDiscarded(discarded: false);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100, packetSequence: 3));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);
    }

    [Fact]
    public async Task RecordingDecodeCanRemainObservableWithoutLivePlayback()
    {
        var backend = new FakeAudioBackend();
        int observedSamples = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, samples) => observedSamples += samples.Length);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "TAR only",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        channel.SetRecordingEnabled(true);

        await coordinator.EnsureDecodeAsync(channel);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100));
        await Task.Delay(40);

        Assert.Equal(AnalogVoicePacketCodec.SamplesPerPacket, observedSamples);
        Assert.Empty(backend.Playback.Frames);
        Assert.Empty(coordinator.LivePlaybackChannels);

        await coordinator.StartAsync(channel);
        await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100, packetSequence: 2));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        Assert.Equal((ChannelId)channel, Assert.Single(coordinator.LivePlaybackChannels));
    }

    [Fact]
    public async Task KeepsCompleteLossConcealmentObservableWhileBoundingLivePlayback()
    {
        var backend = new FakeAudioBackend();
        int observedSamples = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, samples) => observedSamples += samples.Length);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "PHS Scan",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateP25Traffic(100, packetSequence: 1));
        await coordinator.ProcessAsync(channel, CreateP25Traffic(100, packetSequence: 12));

        AudioMixerDiagnostics diagnostics = coordinator.GetPlaybackDiagnostics(channel)!;
        Assert.Equal(108 * VocoderFrameSizes.PcmSamplesPerFrame, observedSamples);
        Assert.True(
            diagnostics.SuppressedLiveConcealmentSamples >=
            81 * VocoderFrameSizes.PcmSamplesPerFrame);
    }

    [Fact]
    public async Task ConcurrentStreamsOnOneTalkgroupUseIndependentVocoderAndMixerLanes()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new DistinctVocoderBackend();
        var observedStreams = new HashSet<uint>();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            samplesObserver: (_, streamId, _, _) =>
            {
                lock (observedStreams)
                    observedStreams.Add(streamId);
            });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "TAC",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 42));
        await WaitForAsync(() =>
        {
            int count = backend.Playback.Frames.Count;
            for (int index = 0; index < count; index++)
            {
                if (backend.Playback.Frames[index][0] >= 2_000)
                    return true;
            }
            return false;
        });

        Assert.Equal(2, vocoder.CreateSessionCalls);
        Assert.Equal(new uint[] { 41, 42 }, observedStreams.Order().ToArray());
        int outputCount = backend.Playback.Frames.Count;
        var outputStarts = new short[outputCount];
        for (int index = 0; index < outputCount; index++)
            outputStarts[index] = backend.Playback.Frames[index][0];
        Assert.True(
            outputStarts.Contains((short)3_000) ||
            (outputStarts.Contains((short)1_000) && outputStarts.Contains((short)2_000)),
            "Each stream must reach playback either in one mixed frame or in adjacent frames.");
    }

    [Fact]
    public async Task EpisodeFragmentsKeepIndependentDecodersButReuseOneMixerLane()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new DistinctVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder);
        coordinator.SetReceivePlaybackEpisodeResolver((_, traffic) =>
            new ReceivePlaybackEpisode(
                900,
                41,
                traffic.StreamId,
                RetainUntilEpisodeCompletion: true));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "TAC",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        await coordinator.ProcessAsync(channel, CreateDmrTerminator(100, 0, streamId: 41));
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 42));

        Assert.Equal(2, vocoder.CreateSessionCalls);
        AudioMixerLaneDiagnostics lane = Assert.Single(
            coordinator.GetPlaybackDiagnostics(channel)!.LaneDiagnostics!);
        Assert.Contains("episode 900", lane.Label, StringComparison.Ordinal);

        await coordinator.CompleteStreamAsync(channel, 42, DateTimeOffset.UtcNow);
        await coordinator.CompleteEpisodeAsync(channel, 900);
    }

    [Fact]
    public async Task EpisodeHandoffSuppressesRetiredLivePcmButKeepsDecodedSamplesObservable()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new DistinctVocoderBackend();
        var observedStreams = new List<uint>();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            samplesObserver: (_, streamId, _, _) => observedStreams.Add(streamId));
        coordinator.SetReceivePlaybackEpisodeResolver((_, traffic) =>
            new ReceivePlaybackEpisode(
                900,
                41,
                traffic.StreamId,
                RetainUntilEpisodeCompletion: true));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "TAC",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 42));
        await coordinator.ProcessAsync(
            channel,
            CreateTraffic(100, 0, packetSequence: 2, streamId: 41));

        Assert.Equal(new uint[] { 41, 42, 41 }, observedStreams);
        Assert.Equal(
            new EpisodeLivePlayoutDiagnostics(
                ProducerHandoffs: 1,
                SuppressedRetiredSamples: 3 * VocoderFrameSizes.PcmSamplesPerFrame),
            coordinator.GetPlaybackArbitrationDiagnostics(channel));

        await coordinator.CompleteStreamAsync(channel, 41, DateTimeOffset.UtcNow);
        await coordinator.CompleteStreamAsync(channel, 42, DateTimeOffset.UtcNow);
        await coordinator.CompleteEpisodeAsync(channel, 900);
    }

    [Fact]
    public async Task ConfirmedTerminatorReleasesAShortStreamBeforeTheStartupCushionIsFull()
    {
        var backend = new QueueReportingAudioBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));
        await Task.Delay(40);
        Assert.Empty(backend.Playback.Frames);

        await coordinator.ProcessAsync(channel, CreateDmrTerminator(100, 0));
        await WaitForAsync(() => backend.Playback.Frames.Count == 3);

        Assert.False(coordinator.IsTrackingStream(channel, 99));
        Assert.Equal(0, coordinator.GetPlaybackDiagnostics(channel)!.DroppedSamples);
    }

    [Fact]
    public async Task VoiceAfterAConfirmedTerminatorUsesANewDecoderOnTheRetainedEpisodeLane()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new DistinctVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder);
        coordinator.SetReceivePlaybackEpisodeResolver((_, traffic) =>
            new ReceivePlaybackEpisode(
                900,
                41,
                traffic.StreamId,
                RetainUntilEpisodeCompletion: true));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        await coordinator.ProcessAsync(channel, CreateDmrTerminator(100, 0, streamId: 41));
        Assert.False(coordinator.IsTrackingStream(channel, 41));

        await coordinator.ProcessAsync(
            channel,
            CreateTraffic(100, 0, packetSequence: 2, streamId: 41));

        Assert.True(coordinator.IsTrackingStream(channel, 41));
        Assert.Equal(2, vocoder.CreateSessionCalls);
        AudioMixerLaneDiagnostics lane = Assert.Single(
            coordinator.GetPlaybackDiagnostics(channel)!.LaneDiagnostics!);
        Assert.Contains("episode 900", lane.Label, StringComparison.Ordinal);

        await coordinator.CompleteStreamAsync(channel, 41, DateTimeOffset.UtcNow);
        await coordinator.CompleteEpisodeAsync(channel, 900);
    }

    [Fact]
    public async Task ReceivePolicyChangesIgnoreCompletedStreamTombstones()
    {
        var backend = new FakeAudioBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        await coordinator.CompleteStreamAsync(
            channel,
            streamId: 41,
            endedAt: DateTimeOffset.UtcNow);
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        await coordinator.SetLivePlaybackEnabledAsync(channel, enabled: false);
        await coordinator.SetGainAsync(channel, 0.75);
        await coordinator.SetBalanceAsync(channel, -0.25);

        Assert.Empty(coordinator.LivePlaybackChannels);
    }

    [Fact]
    public async Task TimedOutStreamRejectsLateVoiceUntilADefinitiveRestart()
    {
        var backend = new FakeAudioBackend();
        int observedSamples = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, samples) => observedSamples += samples.Length);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 41));
        int samplesBeforeTimeout = observedSamples;

        await coordinator.CompleteStreamAsync(
            channel,
            streamId: 41,
            endedAt: DateTimeOffset.UtcNow);
        await coordinator.ProcessAsync(
            channel,
            CreateTraffic(100, 0, packetSequence: 2, streamId: 41));

        Assert.Equal(samplesBeforeTimeout, observedSamples);
        Assert.False(coordinator.IsTrackingStream(channel, 41));

        await coordinator.ProcessAsync(channel, CreateDmrVoiceLcHeader(100, 0, streamId: 41));
        await coordinator.ProcessAsync(
            channel,
            CreateTraffic(100, 0, packetSequence: 3, streamId: 41));

        Assert.True(observedSamples > samplesBeforeTimeout);
        Assert.True(coordinator.IsTrackingStream(channel, 41));
    }

    [Fact]
    public async Task SharesPlaybackAcrossTwoChannelsAndStopsEachSessionIndividually()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var first = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 1",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var second = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 2",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 2
        });

        await coordinator.StartAsync(first);
        await coordinator.StartAsync(second);

        Assert.Equal(2, coordinator.ActiveChannels.Count);
        Assert.True(coordinator.IsActive(first));
        Assert.True(coordinator.IsActive(second));

        Assert.Equal(0, await coordinator.ProcessAsync(first, CreateTraffic(100, 0)));
        Assert.Equal(0, await coordinator.ProcessAsync(second, CreateTraffic(101, 1)));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);
        Assert.All(backend.Playback.Frames, frame => Assert.Equal(160, frame.Length));

        await coordinator.StopAsync(first);

        Assert.Single(coordinator.ActiveChannels);
        Assert.False(coordinator.IsActive(first));
        Assert.True(coordinator.IsActive(second));
        Assert.False(backend.Playback.IsDisposed);

        await coordinator.StopAsync(second);

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(backend.Playback.IsDisposed);
        Assert.True(backend.IsDisposed);
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task OpensEncryptedChannelWithoutAKeyForClearReceive()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "100",
            Mode = "p25",
            Algo = "aes"
        });

        await coordinator.StartAsync(channel);

        Assert.Single(coordinator.ActiveChannels);
        Assert.False(backend.IsDisposed);
        Assert.False(vocoder.IsDisposed);
        Assert.True(channel.CanListen);
        Assert.False(channel.CanTransmit);

        await coordinator.StopAsync(channel);
    }

    [Fact]
    public async Task OpensNxdnReceiveThroughTheMandatoryVocoder()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "NXDN Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "nxdn"
        });

        await coordinator.StartAsync(channel);

        Assert.True(coordinator.IsActive(channel));
        Assert.Equal(VocoderMode.NxdnAmbe, vocoder.LastMode);
        Assert.Equal(1, vocoder.CreateSessionCalls);
    }

    [Fact]
    public async Task RoutesNxdnReceiveThroughTheMandatoryVocoderBackend()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "NXDN Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "nxdn"
        });

        await coordinator.StartAsync(channel);
        Assert.True(coordinator.IsActive(channel));
        Assert.Equal(0, await coordinator.ProcessAsync(channel, CreateNxdnTraffic(100)));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        Assert.Equal(160, backend.Playback.Frames[0].Length);
        Assert.Equal((short)20_000, backend.Playback.Frames[0][0]);
        Assert.Equal(4, vocoder.LastSession!.DecodeCalls);

        await coordinator.StopAsync(channel);
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task RoutesAnalogReceiveWithoutCreatingAVocoderSession()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Analog Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });

        await coordinator.StartAsync(channel);
        Assert.Equal(0, vocoder.CreateSessionCalls);
        Assert.Equal(0, await coordinator.ProcessAsync(channel, CreateAnalogTraffic(100)));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        Assert.Equal(160, backend.Playback.Frames[0].Length);
        await coordinator.StopAsync(channel);
        Assert.True(backend.Playback.IsDisposed);
    }

    [Fact]
    public async Task OpensEncryptedP25WhenTheConfiguredKeyResolves()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        var keyRing = new P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder, keyRing);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "100",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);

        await coordinator.StartAsync(channel);

        Assert.True(channel.CanListen);
        Assert.True(coordinator.IsActive(channel));
        await coordinator.StopAsync(channel);
    }

    [Fact]
    public async Task AppliesMaximumConfiguredChannelGainToSharedPlayback()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            getChannelGain: _ => 4);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Quiet Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });

        await coordinator.StartAsync(channel);
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        Assert.Equal(short.MaxValue, backend.Playback.Frames[0][0]);
    }

    [Fact]
    public async Task AppliesConfiguredChannelBalanceToStereoPlayback()
    {
        var backend = new StereoAudioBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend(),
            getChannelBalance: _ => -1.0);
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Left Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });

        await coordinator.StartAsync(channel);
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));
        await WaitForAsync(() => backend.Playback.Frames.Count > 0);

        Assert.Equal(320, backend.Playback.Frames[0].Length);
        Assert.Equal((short)20_000, backend.Playback.Frames[0][0]);
        Assert.Equal((short)0, backend.Playback.Frames[0][1]);
    }

    [Fact]
    public async Task RoutesChannelsToSeparateConfiguredOutputDevices()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        var defaultChannel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Default",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var alternateChannel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Alternate",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 2
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            getOutputDeviceId: channelId =>
                channelId == alternateChannel ? "alternate" : "output");

        await coordinator.StartAsync(defaultChannel);
        await coordinator.StartAsync(alternateChannel);
        await coordinator.ProcessAsync(defaultChannel, CreateTraffic(100, 0));
        await coordinator.ProcessAsync(alternateChannel, CreateTraffic(101, 1));

        await WaitForAsync(() => backend.Playback.Frames.Count > 0 && backend.AlternatePlayback.Frames.Count > 0);

        Assert.True(backend.Playback.Frames.Count > 0);
        Assert.True(backend.AlternatePlayback.Frames.Count > 0);
        Assert.False(backend.Playback.IsDisposed);
        Assert.False(backend.AlternatePlayback.IsDisposed);

        await coordinator.StopAsync(defaultChannel);
        Assert.True(backend.Playback.IsDisposed);
        Assert.False(backend.AlternatePlayback.IsDisposed);

        await coordinator.StopAsync(alternateChannel);
        Assert.True(backend.AlternatePlayback.IsDisposed);
    }

    [Fact]
    public async Task RecreatesTheAudioRouteAfterAPlaybackDeviceFailure()
    {
        var firstBackend = new RecoveringAudioBackend(failWrites: true);
        var replacementBackend = new RecoveringAudioBackend(failWrites: false);
        int backendIndex = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backendIndex++ == 0 ? firstBackend : replacementBackend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });

        await coordinator.StartAsync(channel);
        var outputFailure = new TaskCompletionSource<ReceiveAudioOutputFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.OutputFailed += failure => outputFailure.TrySetResult(failure);
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));
        await outputFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessAsync(
            channel,
            CreateTraffic(100, 0, packetSequence: 2)));

        Assert.True(await coordinator.TryRecoverAsync(channel));
        Assert.True(coordinator.IsActive(channel));
        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0, streamId: 100));
        await WaitForAsync(() => replacementBackend.Playback.Frames.Count > 0);
        Assert.Equal(160, replacementBackend.Playback.Frames[0].Length);
    }

    [Fact]
    public async Task RouteRecoveryRebuildsAllSelectedChannelsOnOneReplacementBackend()
    {
        var firstBackend = new RecoveringAudioBackend(failWrites: true);
        var replacementBackend = new RecoveringAudioBackend(failWrites: false);
        int backendIndex = 0;
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backendIndex++ == 0 ? firstBackend : replacementBackend,
            () => new FakeVocoderBackend());
        var first = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 1",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var second = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch 2",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 2
        });
        first.SetAudioEnabled(true);
        second.SetAudioEnabled(true);
        await coordinator.StartAsync(first);
        await coordinator.StartAsync(second);

        var outputFailure = new TaskCompletionSource<ReceiveAudioOutputFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.OutputFailed += failure => outputFailure.TrySetResult(failure);
        await coordinator.ProcessAsync(first, CreateTraffic(100, 0));
        await outputFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessAsync(
            first,
            CreateTraffic(100, 0, packetSequence: 2)));

        ReceiveRouteRecoveryResult recovery = await coordinator.RecoverSelectedAsync([first, second]);

        Assert.Equal(2, recovery.Restarted.Count);
        Assert.Empty(recovery.Failed);
        Assert.True(first.IsAudioEnabled);
        Assert.True(second.IsAudioEnabled);
        Assert.True(firstBackend.IsDisposed);
        Assert.True(coordinator.IsActive(first));
        Assert.True(coordinator.IsActive(second));

        await coordinator.ProcessAsync(first, CreateTraffic(100, 0, packetSequence: 3, streamId: 100));
        await coordinator.ProcessAsync(second, CreateTraffic(101, 1, packetSequence: 1, streamId: 200));
        await WaitForAsync(() => replacementBackend.Playback.Frames.Count >= 2);
    }

    [Fact]
    public async Task ReportsOutputFailureWithoutWaitingForAnotherTrafficFrame()
    {
        var failedBackend = new RecoveringAudioBackend(failWrites: true);
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => failedBackend,
            () => new FakeVocoderBackend());
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var failure = new TaskCompletionSource<ReceiveAudioOutputFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.OutputFailed += observed => failure.TrySetResult(observed);
        await coordinator.StartAsync(channel);

        await coordinator.ProcessAsync(channel, CreateTraffic(100, 0));

        ReceiveAudioOutputFailure observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains(channel, observed.AffectedChannels);
        Assert.IsType<IOException>(observed.Exception);
    }

    [Fact]
    public async Task RouteRecoveryDoesNotInterruptASeparateOutputDevice()
    {
        var failedBackend = new RecoveringAudioBackend(failWrites: true);
        var alternateBackend = new FakeAudioBackend();
        var replacementBackend = new RecoveringAudioBackend(failWrites: false);
        IAudioBackend[] backends = [failedBackend, alternateBackend, replacementBackend];
        int backendIndex = 0;
        var failed = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Failed route",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var unaffected = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Separate route",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 2
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backends[backendIndex++],
            () => new FakeVocoderBackend(),
            getOutputDeviceId: channelId => channelId == unaffected ? "alternate" : null);
        await coordinator.StartAsync(failed);
        await coordinator.StartAsync(unaffected);
        await coordinator.ProcessAsync(unaffected, CreateTraffic(101, 1, streamId: 200));
        await WaitForAsync(() => alternateBackend.AlternatePlayback.Frames.Count > 0);

        var outputFailure = new TaskCompletionSource<ReceiveAudioOutputFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.OutputFailed += failure => outputFailure.TrySetResult(failure);
        await coordinator.ProcessAsync(failed, CreateTraffic(100, 0));
        await outputFailure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessAsync(
            failed,
            CreateTraffic(100, 0, packetSequence: 2)));
        ReceiveRouteRecoveryResult recovery = await coordinator.RecoverSelectedAsync([failed]);

        Assert.Single(recovery.Restarted);
        Assert.Equal((ChannelId)failed, recovery.Restarted[0]);
        Assert.True(coordinator.IsActive(unaffected));
        Assert.False(alternateBackend.AlternatePlayback.IsDisposed);
        int framesBefore = alternateBackend.AlternatePlayback.Frames.Count;
        await coordinator.ProcessAsync(unaffected, CreateTraffic(101, 1, packetSequence: 2, streamId: 200));
        await WaitForAsync(() => alternateBackend.AlternatePlayback.Frames.Count > framesBefore);
        await coordinator.ProcessAsync(failed, CreateTraffic(100, 0, packetSequence: 3));
        await WaitForAsync(() => replacementBackend.Playback.Frames.Count > 0);
    }

    [Fact]
    public async Task RefreshesAReceiveRouteThatFollowsTheSystemDefault()
    {
        var builtInBackend = new RoutingAudioBackend(defaultDeviceId: "built-in");
        var headsetBackend = new RoutingAudioBackend(defaultDeviceId: "headset");
        IAudioBackend[] backends = [builtInBackend, headsetBackend];
        int backendIndex = 0;
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backends[backendIndex++],
            () => new FakeVocoderBackend(),
            getOutputDeviceId: _ => "default");
        await coordinator.StartAsync(channel);
        Assert.Equal("built-in", builtInBackend.LastOutputDeviceId);

        ReceiveRouteRecoveryResult result = await coordinator.RefreshSystemDefaultOutputAsync();

        Assert.Single(result.Restarted);
        Assert.Empty(result.Failed);
        Assert.True(builtInBackend.IsDisposed);
        Assert.Equal("headset", headsetBackend.LastOutputDeviceId);
        Assert.True(coordinator.IsActive(channel));
    }

    [Fact]
    public async Task DoesNotRefreshAFixedReceiveOutput()
    {
        var backend = new RoutingAudioBackend(defaultDeviceId: "built-in");
        int backendCreations = 0;
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () =>
            {
                backendCreations++;
                return backend;
            },
            () => new FakeVocoderBackend(),
            getOutputDeviceId: _ => "built-in");
        await coordinator.StartAsync(channel);

        ReceiveRouteRecoveryResult result = await coordinator.RefreshSystemDefaultOutputAsync();

        Assert.Empty(result.Restarted);
        Assert.Empty(result.Failed);
        Assert.Equal(1, backendCreations);
        Assert.False(backend.IsDisposed);
        Assert.True(coordinator.IsActive(channel));
    }

    [Fact]
    public async Task DefaultRefreshDoesNotInterruptAFixedSessionOnTheOldEndpoint()
    {
        var sharedBackend = new RoutingAudioBackend(defaultDeviceId: "built-in");
        var duplicateBackend = new RoutingAudioBackend(defaultDeviceId: "built-in");
        var headsetBackend = new RoutingAudioBackend(defaultDeviceId: "headset");
        IAudioBackend[] backends = [sharedBackend, duplicateBackend, headsetBackend];
        int backendIndex = 0;
        var fixedChannel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Fixed",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        var defaultChannel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Default",
            System = "System 1",
            Tgid = "101",
            Mode = "analog"
        });
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backends[backendIndex++],
            () => new FakeVocoderBackend(),
            getOutputDeviceId: channelId => channelId == fixedChannel ? "built-in" : "default");
        await coordinator.StartAsync(fixedChannel);
        await coordinator.StartAsync(defaultChannel);
        Assert.True(duplicateBackend.IsDisposed);

        ReceiveRouteRecoveryResult result = await coordinator.RefreshSystemDefaultOutputAsync();

        Assert.Single(result.Restarted);
        Assert.Equal((ChannelId)defaultChannel, result.Restarted[0]);
        Assert.False(sharedBackend.IsDisposed);
        Assert.True(coordinator.IsActive(fixedChannel));
        Assert.True(coordinator.IsActive(defaultChannel));
        Assert.Equal("headset", headsetBackend.LastOutputDeviceId);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheSharedReceiveCleanup()
    {
        var backend = new BlockingDrainAudioBackend();
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "analog"
        });
        var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => new FakeVocoderBackend());
        await coordinator.StartAsync(channel);

        Task first = coordinator.DisposeAsync().AsTask();
        await backend.Playback.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task second = coordinator.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        backend.Playback.AllowDrain();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(backend.IsDisposed);
        Assert.Equal(1, backend.Playback.DisposeCalls);
    }

    [Fact]
    public async Task StopDetachesAReceiveSessionWithoutWaitingForANonCooperativeDecoder()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new NonCooperativeVocoderBackend();
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        var coordinator = new ChannelReceiveAudioCoordinator(() => backend, () => vocoder);
        await coordinator.StartAsync(channel);
        Task<int> processing = Task.Run(() => coordinator.ProcessAsync(channel, CreateTraffic(100, 0)));
        try
        {
            await vocoder.Session.DecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task stop = coordinator.StopAsync(channel);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(coordinator.IsActive(channel));
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            vocoder.Session.ReleaseDecode.TrySetResult();
        }
        try
        {
            await processing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (
            exception.GetBaseException() is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The retirement token is expected to win after the decoder returns.
        }
        await WaitForAsync(() => vocoder.Session.IsDisposed);
        await coordinator.DisposeAsync();
    }

    private static FneTrafficFrame CreateTraffic(
        uint destinationId,
        byte slot,
        ushort packetSequence = 1,
        uint streamId = 99)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence,
            streamId,
            payload: new byte[DmrVoicePacketCodec.PacketBytes]);
    }

    private static FneTrafficFrame CreateDmrTerminator(
        uint destinationId,
        byte slot,
        ushort packetSequence = 2,
        uint streamId = 99)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot,
            callType: "GROUP",
            frameType: "TERMINATOR",
            subtype: "TERMINATOR_WITH_LC",
            packetSequence,
            streamId,
            payload: []);

    private static FneTrafficFrame CreateDmrVoiceLcHeader(
        uint destinationId,
        byte slot,
        ushort packetSequence = 1,
        uint streamId = 99)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot,
            callType: "GROUP",
            frameType: "DATA_SYNC",
            subtype: "VOICE_LC_HEADER",
            packetSequence,
            streamId,
            payload: []);

    private static FneTrafficFrame CreateAnalogTraffic(
        uint destinationId,
        ushort packetSequence = 1)
    {
        var samples = new short[AnalogVoicePacketCodec.SamplesPerPacket];
        return new FneTrafficFrame(
            FneTrafficProtocol.Analog,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence,
            streamId: 99,
            payload: AnalogVoicePacketCodec.CreatePacket(
                AnalogAudioFrameType.Voice,
                (byte)packetSequence,
                destinationId,
                samples));
    }

    private static FneTrafficFrame CreateP25Traffic(
        uint destinationId,
        ushort packetSequence)
    {
        int[] lengths = [22, 14, 17, 17, 17, 17, 17, 17, 16];
        int[] offsets = [10, 1, 5, 5, 5, 5, 5, 5, 4];
        byte[] payload = new byte[P25DfsiFrameCodec.HeaderBytes + P25DfsiFrameCodec.RecordBytes];
        payload[P25DfsiFrameCodec.RecordLengthOffset] = (byte)payload.Length;

        int offset = P25DfsiFrameCodec.HeaderBytes;
        for (int index = 0; index < lengths.Length; index++)
        {
            payload[offset] = (byte)(0x62 + index);
            for (int codewordByte = 0; codewordByte < P25DfsiFrameCodec.CodewordBytes; codewordByte++)
                payload[offset + offsets[index] + codewordByte] = (byte)(index + 1);
            offset += lengths[index];
        }

        return new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence,
            streamId: 99,
            payload);
    }

    private static FneTrafficFrame CreateNxdnTraffic(uint destinationId)
    {
        byte[] ambe = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)value)
            .ToArray();
        return new FneTrafficFrame(
            FneTrafficProtocol.Nxdn,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence: 1,
            streamId: 99,
            payload: NxdnVoicePacketCodec.CreateVoicePacket(2, destinationId, true, 0, ambe));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);

        Assert.True(condition());
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new();
        public FakePlayback AlternatePlayback { get; } = new();
        public bool IsDisposed { get; private set; }
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        {
            return direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo("output", "Fake output", direction, true),
                    new AudioDeviceInfo("alternate", "Fake alternate output", direction, false)
                ]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];
        }

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => device.Id == "alternate" ? AlternatePlayback : Playback;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class QueueReportingAudioBackend : IAudioBackend
    {
        public QueueReportingPlayback Playback { get; } = new();
        public string Name => "queue-reporting-fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("output", "Queue-reporting output", direction, true)]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => Playback;

        public void Dispose()
        {
        }
    }

    private sealed class RecoveringAudioBackend(bool failWrites) : IAudioBackend
    {
        public RecoveringPlayback Playback { get; } = new(failWrites);
        public bool IsDisposed { get; private set; }
        public string Name => "recovering-fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("output", "Recovering output", direction, true)]
                : [new AudioDeviceInfo("input", "Recovering input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => Playback;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RoutingAudioBackend(string defaultDeviceId) : IAudioBackend
    {
        public string? LastOutputDeviceId { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "routing-fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [
                    new AudioDeviceInfo(
                        "built-in",
                        "Built-in output",
                        direction,
                        defaultDeviceId == "built-in"),
                    new AudioDeviceInfo(
                        "headset",
                        "Headset output",
                        direction,
                        defaultDeviceId == "headset")
                ]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            LastOutputDeviceId = device.Id;
            return new FakePlayback(format);
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingDrainAudioBackend : IAudioBackend
    {
        public BlockingDrainPlayback Playback { get; } = new();
        public string Name => "blocking-drain-fake";
        public bool IsDisposed { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("output", "Blocking output", direction, true)]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => Playback;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingDrainPlayback : IAudioPlayback
    {
        private readonly TaskCompletionSource drainCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzStereo16Bit;
        public TaskCompletionSource DrainEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            DrainEntered.TrySetResult();
            await drainCompletion.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        public void AllowDrain() => drainCompletion.TrySetResult();
    }

    private sealed class StereoAudioBackend : IAudioBackend
    {
        public FakePlayback Playback { get; } = new(PcmAudioFormat.Voice8KhzStereo16Bit);
        public string Name => "stereo-fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Output
                ? [new AudioDeviceInfo("output", "Stereo output", direction, true)]
                : [new AudioDeviceInfo("input", "Fake input", direction, true)];

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => Playback;

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public FakePlayback(PcmAudioFormat? format = null)
        {
            Format = format ?? PcmAudioFormat.Voice8KhzMono16Bit;
        }

        public TaskCompletionSource<short[]> NonSilent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public PcmAudioFormat Format { get; }

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            short[] frame = samples.ToArray();
            Frames.Add(frame);
            if (frame.Any(sample => sample != 0)) NonSilent.TrySetResult(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueReportingPlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;
        public int? QueuedSamples { get; private set; } = 0;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            QueuedSamples += samples.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecoveringPlayback(bool failWrites) : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failWrites)
                throw new IOException("The output audio device was removed.");
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public int CreateSessionCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public VocoderMode? LastMode { get; private set; }
        public FakeVocoderSession? LastSession { get; private set; }
        public string Name => "fake";
        public bool IsAvailable => true;

        public IVocoderSession CreateSession(VocoderMode mode)
        {
            CreateSessionCalls++;
            LastMode = mode;
            return LastSession = new FakeVocoderSession();
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingFirstVocoderBackend : IVocoderBackend
    {
        private int sessionCount;

        public TaskCompletionSource FirstDecodeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstDecode { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "blocking-first";
        public bool IsAvailable => true;

        public IVocoderSession CreateSession(VocoderMode mode)
            => Interlocked.Increment(ref sessionCount) == 1
                ? new BlockingVocoderSession(FirstDecodeStarted, ReleaseFirstDecode)
                : new FakeVocoderSession();

        public void Dispose()
        {
            ReleaseFirstDecode.TrySetResult();
        }
    }

    private sealed class DistinctVocoderBackend : IVocoderBackend
    {
        public int CreateSessionCalls { get; private set; }
        public string Name => "distinct";
        public bool IsAvailable => true;

        public IVocoderSession CreateSession(VocoderMode mode)
            => new ConstantVocoderSession((short)(++CreateSessionCalls * 1_000));

        public void Dispose()
        {
        }
    }

    private sealed class ConstantVocoderSession(short value) : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            samples.Fill(value);
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingVocoderSession(
        TaskCompletionSource started,
        TaskCompletionSource release) : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            samples.Fill(20_000);
            return 0;
        }

        public void Dispose()
        {
            release.TrySetResult();
        }
    }

    private sealed class NonCooperativeVocoderBackend : IVocoderBackend
    {
        public NonCooperativeVocoderSession Session { get; } = new();
        public string Name => "non-cooperative";
        public bool IsAvailable => true;
        public IVocoderSession CreateSession(VocoderMode mode) => Session;
        public void Dispose() { }
    }

    private sealed class NonCooperativeVocoderSession : IVocoderSession
    {
        public TaskCompletionSource DecodeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDecode { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeStarted.TrySetResult();
            ReleaseDecode.Task.GetAwaiter().GetResult();
            samples.Clear();
            return 0;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill(20_000);
            return 0;
        }

        public void Dispose()
        {
        }
    }

}
