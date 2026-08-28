using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TransmitCoordinatorTests
{
    [Fact]
    public async Task StartRejectsAChannelWithActiveReceivePlayback()
    {
        var channel = Channel("A", 100);
        channel.SetAudioEnabled(true);
        channel.MarkReceivePlaybackActive(sourceId: 42, streamId: 7);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(channel, endpoint));

        Assert.Contains("currently receiving", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, audio.OpenCaptureCalls);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task AnalogMultiTargetUsesOneCaptureAndCleansUpAllCalls()
    {
        var first = Channel("A", 100);
        var second = Channel("B", 101);
        var endpoint = new FakeEndpoint("Test", [first, second]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync([new TransmitTarget(first, endpoint), new TransmitTarget(second, endpoint)]);
        await coordinator.ActivateAsync();

        Assert.Equal(1, audio.OpenCaptureCalls);
        Assert.Equal(2, coordinator.ActiveChannels.Count);
        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => endpoint.Sent.Count == 2);
        Assert.Equal(2, endpoint.Sent.Count); // one voice packet per target

        await coordinator.StopAsync();

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(4, endpoint.Sent.Count); // matching terminators
    }

    [Fact]
    public async Task SampleObservationUsesAStableSnapshotWhileTransmitStops()
    {
        var first = Channel("A", 100);
        var second = Channel("B", 101);
        var endpoint = new FakeEndpoint("Test", [first, second]);
        var audio = new FakeAudioBackend();
        var firstObservationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowObservationToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<ChannelViewModel>();
        await using var coordinator = new ChannelTransmitCoordinator(
            samplesObserver: (channel, _, _, _) =>
            {
                observed.Add(channel);
                if (ReferenceEquals(channel, first))
                {
                    firstObservationEntered.TrySetResult();
                    allowObservationToContinue.Task.GetAwaiter().GetResult();
                }
            },
            createAudioBackend: () => audio);
        await coordinator.StartAsync([
            new TransmitTarget(first, endpoint),
            new TransmitTarget(second, endpoint)]);
        await coordinator.ActivateAsync();

        Task publish = Task.Run(() => audio.Capture.Emit(new short[160]));
        await firstObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        allowObservationToContinue.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([first, second], observed);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task SuppressedMicrophoneFramesAreDroppedUntilOperatorAudioMayTransmit()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var observed = new List<short[]>();
        await using var coordinator = new ChannelTransmitCoordinator(
            samplesObserver: (_, _, _, samples) => observed.Add(samples.ToArray()),
            createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        await coordinator.ActivateAsync();
        int startupFrameCount = endpoint.Sent.Count;
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());

        Assert.Equal(startupFrameCount, endpoint.Sent.Count);
        Assert.Empty(observed);

        await coordinator.ReleaseMicrophoneAudioAsync(requireFreshRecoveryCallback: false);
        audio.Capture.Emit(Enumerable.Repeat((short)2000, 160).ToArray());
        await WaitForAsync(() => endpoint.Sent.Count == startupFrameCount + 1);

        Assert.Equal(startupFrameCount + 1, endpoint.Sent.Count);
        Assert.Single(observed);
        Assert.All(observed[0], sample => Assert.Equal((short)2000, sample));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public async Task PreflightIdentifiesColdBluetoothOrUnknownTransitions(
        bool? inputIsBluetooth,
        bool expectedGate)
    {
        var audio = new FakeAudioBackend(inputIsBluetooth: inputIsBluetooth);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        MicrophoneStartExpectation expectation =
            await coordinator.InspectNextMicrophoneStartAsync();

        Assert.True(expectation.StartsCold);
        Assert.Equal(inputIsBluetooth, expectation.IsBluetooth);
        Assert.Equal(expectedGate, expectation.RequiresReceiveTransitionGate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ColdBluetoothOrUnknownMicrophoneReadinessUsesFirstSelectedCaptureSample(
        bool? inputIsBluetooth)
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: inputIsBluetooth);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        Assert.True(coordinator.ActiveMicrophoneStartedCold);
        Assert.Equal(inputIsBluetooth, coordinator.ActiveMicrophoneIsBluetooth);
        Task ready = coordinator.WaitForMicrophoneReadyAsync(TimeSpan.FromSeconds(1));
        Assert.False(ready.IsCompleted);

        audio.Capture.Emit(new short[160]);
        await ready;
        await coordinator.StopAsync();
        Assert.False(coordinator.ActiveMicrophoneStartedCold);
        Assert.Null(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task KnownNonBluetoothMicrophoneReadinessUsesFirstSelectedCaptureSample()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: false);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        Task ready = coordinator.WaitForMicrophoneReadyAsync(TimeSpan.FromSeconds(1));

        audio.Capture.Emit(new short[160]);

        await ready;
        Assert.False(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Theory]
    [InlineData(false, "stale")]
    [InlineData(true, "faulted")]
    public async Task ActiveTransmitFailsClosedWhenFreshMicrophoneProgressStops(
        bool stopCapture,
        string expectedState)
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: false);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;
        coordinator.SetMicrophoneAudioSuppressed(false);
        int sentBeforeFailure = endpoint.Sent.Count;

        if (stopCapture)
            await audio.Capture.StopAsync();

        Exception failure = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        audio.Capture.Emit(new short[160]);

        Assert.IsType<IOException>(failure);
        Assert.Contains(expectedState, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.Equal(sentBeforeFailure, endpoint.Sent.Count);
    }

    [Fact]
    public async Task StartupGateAllowsColdBluetoothPermitTransitionWithoutFaultingTransmit()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;

        // Opening and warming the post-transition Bluetooth output may pause
        // input callbacks longer than the normal active-TX stale threshold.
        await Task.Delay(120);

        Assert.False(faulted.Task.IsCompleted);
        Assert.Single(coordinator.ActiveChannels);

        // Closing the permit-tone output may be the event that allows capture
        // to resume. Keep operator audio gated until the first callback that
        // occurs after the cue has completed.
        Task<TimeSpan> release = coordinator.ReleaseMicrophoneAudioAsync(
            requireFreshRecoveryCallback: true,
            recoveryTimeout: TimeSpan.FromSeconds(1));
        await Task.Delay(120);
        Assert.False(release.IsCompleted);
        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.False(faulted.Task.IsCompleted);

        audio.Capture.Emit(new short[160]);
        TimeSpan recovery = await release;
        await Task.Delay(20);

        Assert.True(recovery >= TimeSpan.Zero);
        Assert.False(faulted.Task.IsCompleted);
        Assert.False(coordinator.IsMicrophoneAudioSuppressed);
    }

    [Fact]
    public async Task ColdBluetoothPostCueRecoveryTimesOutWithoutReleasingOperatorAudio()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel, endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.ReleaseMicrophoneAudioAsync(
                requireFreshRecoveryCallback: true,
                recoveryTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.False(faulted.Task.IsCompleted);
        Assert.Single(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task PreflightRejectionDoesNotOpenAudio()
    {
        var receiveOnly = new ChannelViewModel(new ChannelConfiguration { Name = "RX", System = "Test", Tgid = "100", Mode = "analog", RxOnly = true });
        var endpoint = new FakeEndpoint("Test", [receiveOnly]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(receiveOnly, endpoint));

        Assert.Equal(0, audio.OpenCaptureCalls);
        Assert.Empty(endpoint.Sent);
    }

    [Theory]
    [InlineData("dmr", FneTrafficProtocol.Dmr)]
    [InlineData("p25", FneTrafficProtocol.P25)]
    [InlineData("nxdn", FneTrafficProtocol.Nxdn)]
    public async Task DigitalModesCreateTheMatchingProtocolPipeline(string mode, FneTrafficProtocol expectedProtocol)
    {
        var channel = Channel("Digital", 100, mode);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => vocoder);

        await Task.Run(() => coordinator.StartAsync(channel, endpoint));
        await coordinator.ActivateAsync();
        audio.Capture.Emit(new short[160]);

        Assert.True(vocoder.CreateSessionCalls > 0);
        Assert.Contains(endpoint.Sent, sent => sent.Protocol == expectedProtocol);
        await coordinator.StopAsync();
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task PreparedDigitalCallEmitsNothingUntilExplicitActivation()
    {
        var channel = Channel("Digital", 100, "dmr");
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => new FakeVocoderBackend());

        await coordinator.StartAsync(channel, endpoint);
        audio.Capture.Emit(new short[480]);
        Assert.Empty(endpoint.Sent);

        await coordinator.ActivateAsync();
        Assert.Single(endpoint.Sent);
        Assert.True(endpoint.Sent.TryPeek(out var firstPacket));
        Assert.Equal(FneTrafficProtocol.Dmr, firstPacket.Protocol);

        audio.Capture.Emit(new short[480]);
        await WaitForAsync(() => endpoint.Sent.Count == 2);
        Assert.Equal(2, endpoint.Sent.Count);
    }

    [Theory]
    [InlineData("dmr")]
    [InlineData("p25")]
    [InlineData("nxdn")]
    public async Task DigitalModeStartupFailureRollsBackAfterBackgroundStart(string mode)
    {
        var channel = Channel("Digital", 100, mode);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend(failCreateSession: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => vocoder);

        await Assert.ThrowsAsync<IOException>(() =>
            Task.Run(() => coordinator.StartAsync(channel, endpoint)));

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task CaptureStartFailureRollsBackCreatedSessionsAndInfrastructure()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(failStart: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(channel, endpoint));

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task WarmMicrophoneStaysRunningBetweenCallsUntilDisabled()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        Assert.True(audio.Capture.IsRunning);
        Assert.Equal(1, audio.OpenCaptureCalls);

        audio.Capture.Emit(new short[160]);

        await coordinator.StartAsync(channel, endpoint);
        Assert.False(coordinator.ActiveMicrophoneStartedCold);
        await coordinator.StopAsync();

        Assert.True(audio.Capture.IsRunning);
        Assert.False(audio.Capture.IsDisposed);
        Assert.False(audio.IsDisposed);

        await coordinator.SetKeepMicrophoneWarmAsync(false);

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task UnsettledWarmMicrophoneStillUsesColdPermitPolicy()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel, endpoint);

        Assert.True(coordinator.ActiveMicrophoneStartedCold);
        Assert.True(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task StaleWarmMicrophoneIsRestartedBeforePreflight()
    {
        var firstAudio = new FakeAudioBackend(inputDeviceId: "first");
        var replacementAudio = new FakeAudioBackend(inputDeviceId: "replacement");
        IAudioBackend[] backends = [firstAudio, replacementAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => backends[backendIndex++],
            microphoneStaleAfter: TimeSpan.FromMilliseconds(20));
        await coordinator.SetKeepMicrophoneWarmAsync(true);
        firstAudio.Capture.Emit(new short[160]);
        await Task.Delay(35);

        MicrophoneStartExpectation expectation =
            await coordinator.InspectNextMicrophoneStartAsync();

        Assert.True(expectation.StartsCold);
        Assert.True(firstAudio.Capture.IsDisposed);
        Assert.True(replacementAudio.Capture.IsRunning);
        Assert.Equal(2, backendIndex);
    }

    [Fact]
    public async Task ReportsPhysicalBluetoothInputForPermitPolicy()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync(channel, endpoint);

        Assert.True(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task RefreshesAWarmSystemDefaultMicrophone()
    {
        var firstAudio = new FakeAudioBackend(inputDeviceId: "built-in");
        var headsetAudio = new FakeAudioBackend(inputDeviceId: "headset");
        IAudioBackend[] backends = [firstAudio, headsetAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "default" },
            createAudioBackend: () => backends[backendIndex++]);
        await coordinator.SetKeepMicrophoneWarmAsync(true);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.Refreshed, result);
        Assert.True(firstAudio.Capture.IsDisposed);
        Assert.True(firstAudio.IsDisposed);
        Assert.True(headsetAudio.Capture.IsRunning);
        Assert.Equal("headset", headsetAudio.LastInputDeviceId);
    }

    [Fact]
    public async Task DefersDefaultMicrophoneRefreshUntilPttEnds()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var firstAudio = new FakeAudioBackend(inputDeviceId: "built-in");
        var headsetAudio = new FakeAudioBackend(inputDeviceId: "headset");
        IAudioBackend[] backends = [firstAudio, headsetAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "default" },
            createAudioBackend: () => backends[backendIndex++]);
        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel, endpoint);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.DeferredUntilIdle, result);
        Assert.False(firstAudio.IsDisposed);
        await coordinator.StopAsync();
        Assert.True(firstAudio.IsDisposed);
        Assert.True(headsetAudio.Capture.IsRunning);
        Assert.Equal("headset", headsetAudio.LastInputDeviceId);
    }

    [Fact]
    public async Task DoesNotRefreshAFixedMicrophone()
    {
        var audio = new FakeAudioBackend(inputDeviceId: "fixed-input");
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "fixed-input" },
            createAudioBackend: () => audio);
        await coordinator.SetKeepMicrophoneWarmAsync(true);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.NotRequired, result);
        Assert.False(audio.IsDisposed);
        Assert.True(audio.Capture.IsRunning);
    }

    [Fact]
    public async Task DisablingWarmMicrophoneDuringTransmitPreservesTheActiveLease()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel, endpoint);
        await coordinator.ActivateAsync();
        int before = endpoint.Sent.Count;

        await coordinator.SetKeepMicrophoneWarmAsync(false);
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());
        await WaitForAsync(() => endpoint.Sent.Count == before + 1);

        Assert.True(audio.Capture.IsRunning);
        Assert.False(audio.Capture.IsDisposed);
        Assert.False(audio.IsDisposed);
        Assert.Equal(before + 1, endpoint.Sent.Count);

        await coordinator.StopAsync();
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task WarmMicrophoneStartFailureRollsBackInfrastructure()
    {
        var audio = new FakeAudioBackend(failStart: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SetKeepMicrophoneWarmAsync(true));

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task ReportsHighQualityBluetoothStatusAfterCaptureStarts()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(
            highQualityBluetoothStatus: HighQualityBluetoothAudioStatus.Active);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);
        HighQualityBluetoothAudioStatus? reported = null;
        coordinator.HighQualityBluetoothStatusChanged += (_, status) => reported = status;

        await coordinator.StartAsync(channel, endpoint);

        Assert.Equal(HighQualityBluetoothAudioStatus.Active, reported);
    }

    [Fact]
    public async Task SessionFaultIsReportedAndCleanupRemainsSafe()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel], throwOnSend: true);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);
        Exception? fault = null;
        coordinator.Faulted += (_, exception) => fault = exception;

        await coordinator.StartAsync(channel, endpoint);
        await coordinator.ActivateAsync();
        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => fault is not null);
        await coordinator.StopAsync();

        Assert.IsType<IOException>(fault);
        Assert.True(audio.Capture.IsDisposed);
    }

    private static ChannelViewModel Channel(string name, uint tgid, string mode = "analog") => new(new ChannelConfiguration
    {
        Name = name,
        System = "Test",
        Tgid = tgid.ToString(),
        Mode = mode,
        Slot = 1
    });

    private sealed class FakeEndpoint(string name, IReadOnlyList<ChannelViewModel> channels, bool throwOnSend = false) : IFneTrafficEndpoint
    {
        private uint nextStreamId;
        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public ConcurrentQueue<(FneTrafficProtocol Protocol, uint StreamId)> Sent { get; } = [];
        public uint CreateStreamId() => ++nextStreamId;
        public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort sequence, uint streamId)
        {
            if (throwOnSend)
                throw new IOException("test transport fault");
            Sent.Enqueue((protocol, streamId));
        }
    }

    private sealed class FakeAudioBackend(
        bool failStart = false,
        HighQualityBluetoothAudioStatus highQualityBluetoothStatus = HighQualityBluetoothAudioStatus.Off,
        string inputDeviceId = "input",
        bool? inputIsBluetooth = false)
        : IAudioBackend, IHighQualityBluetoothAudioStatus
    {
        public FakeCapture Capture { get; } = new(failStart);
        public int OpenCaptureCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string? LastInputDeviceId { get; private set; }
        public string Name => "test";
        public HighQualityBluetoothAudioStatus HighQualityBluetoothStatus => highQualityBluetoothStatus;
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new AudioDeviceInfo(
                direction == AudioDirection.Input ? inputDeviceId : "output",
                "Test",
                direction,
                true,
                direction == AudioDirection.Input ? inputIsBluetooth : false)];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
        {
            OpenCaptureCalls++;
            LastInputDeviceId = device.Id;
            return Capture;
        }
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeCapture(bool failStart = false) : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            if (failStart)
                throw new IOException("test capture start failure");
            IsRunning = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
        public void Emit(short[] samples) => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }

    private sealed class FakeVocoderBackend(bool failCreateSession = false) : IVocoderBackend
    {
        public int CreateSessionCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "test";
        public bool IsAvailable => !IsDisposed;
        public IVocoderSession CreateSession(VocoderMode mode)
        {
            CreateSessionCalls++;
            if (failCreateSession)
                throw new IOException("test vocoder startup failure");
            return new FakeVocoderSession();
        }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) { codeword.Fill(0x42); return 0; }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }
}
