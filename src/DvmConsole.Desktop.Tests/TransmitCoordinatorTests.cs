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
    public async Task AnalogMultiTargetUsesOneCaptureAndCleansUpAllCalls()
    {
        var first = Channel("A", 100);
        var second = Channel("B", 101);
        var endpoint = new FakeEndpoint("Test", [first, second]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync([new TransmitTarget(first, endpoint), new TransmitTarget(second, endpoint)]);

        Assert.Equal(1, audio.OpenCaptureCalls);
        Assert.Equal(2, coordinator.ActiveChannels.Count);
        audio.Capture.Emit(new short[160]);
        Assert.Equal(2, endpoint.Sent.Count); // one voice packet per target

        await coordinator.StopAsync();

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(4, endpoint.Sent.Count); // matching terminators
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
        int startupFrameCount = endpoint.Sent.Count;
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());

        Assert.Equal(startupFrameCount, endpoint.Sent.Count);
        Assert.Empty(observed);

        coordinator.SetMicrophoneAudioSuppressed(false);
        audio.Capture.Emit(Enumerable.Repeat((short)2000, 160).ToArray());

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
    public async Task ColdBluetoothOrUnknownMicrophoneReadinessWaitsForSustainedSelectedCaptureSamples(
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
        Assert.False(ready.IsCompleted);
        int requiredSamples = checked((int)Math.Ceiling(
            PcmAudioFormat.Voice8KhzMono16Bit.SampleRate *
            ChannelTransmitCoordinator.ColdMicrophoneSettlingDuration.TotalSeconds));
        audio.Capture.Emit(new short[requiredSamples - 160]);

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
        audio.Capture.Emit(new short[160]);

        Assert.True(vocoder.CreateSessionCalls > 0);
        Assert.Contains(endpoint.Sent, sent => sent.Protocol == expectedProtocol);
        await coordinator.StopAsync();
        Assert.True(vocoder.IsDisposed);
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
        int before = endpoint.Sent.Count;

        await coordinator.SetKeepMicrophoneWarmAsync(false);
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());

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
        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => fault is not null);
        await coordinator.StopAsync();

        Assert.IsType<IOException>(fault);
        Assert.True(audio.Capture.IsDisposed);
    }

    private static ChannelViewModel Channel(string name, uint tgid, string mode = "analog") => new(new ChannelConfiguration
    {
        Name = name, System = "Test", Tgid = tgid.ToString(), Mode = mode, Slot = 1
    });

    private sealed class FakeEndpoint(string name, IReadOnlyList<ChannelViewModel> channels, bool throwOnSend = false) : IFneTrafficEndpoint
    {
        private uint nextStreamId;
        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public List<(FneTrafficProtocol Protocol, uint StreamId)> Sent { get; } = [];
        public uint CreateStreamId() => ++nextStreamId;
        public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort sequence, uint streamId)
        {
            if (throwOnSend)
                throw new IOException("test transport fault");
            Sent.Add((protocol, streamId));
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
