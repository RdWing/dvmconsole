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
    public async Task DigitalModesCreateTheMatchingProtocolPipeline(string mode, FneTrafficProtocol expectedProtocol)
    {
        var channel = Channel("Digital", 100, mode);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => vocoder);

        await coordinator.StartAsync(channel, endpoint);
        audio.Capture.Emit(new short[160]);

        Assert.True(vocoder.CreateSessionCalls > 0);
        Assert.Contains(endpoint.Sent, sent => sent.Protocol == expectedProtocol);
        await coordinator.StopAsync();
        Assert.True(vocoder.IsDisposed);
    }

    private static ChannelViewModel Channel(string name, uint tgid, string mode = "analog") => new(new ChannelConfiguration
    {
        Name = name, System = "Test", Tgid = tgid.ToString(), Mode = mode, Slot = 1
    });

    private sealed class FakeEndpoint(string name, IReadOnlyList<ChannelViewModel> channels) : IFneTrafficEndpoint
    {
        private uint nextStreamId;
        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public List<(FneTrafficProtocol Protocol, uint StreamId)> Sent { get; } = [];
        public uint CreateStreamId() => ++nextStreamId;
        public void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort sequence, uint streamId)
            => Sent.Add((protocol, streamId));
    }

    private sealed class FakeAudioBackend : IAudioBackend
    {
        public FakeCapture Capture { get; } = new();
        public int OpenCaptureCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "test";
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new AudioDeviceInfo(direction == AudioDirection.Input ? "input" : "output", "Test", direction, true)];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format) { OpenCaptureCalls++; return Capture; }
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) { IsRunning = true; return ValueTask.CompletedTask; }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { IsDisposed = true; return ValueTask.CompletedTask; }
        public void Emit(short[] samples) => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public int CreateSessionCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "test";
        public bool IsAvailable => !IsDisposed;
        public IVocoderSession CreateSession(VocoderMode mode) { CreateSessionCalls++; return new FakeVocoderSession(); }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) { codeword.Fill(0x42); return 0; }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { }
    }
}
