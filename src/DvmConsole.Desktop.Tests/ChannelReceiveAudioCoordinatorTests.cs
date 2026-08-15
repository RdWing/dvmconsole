using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelReceiveAudioCoordinatorTests
{
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
    public async Task RejectsEncryptedReceiveBeforeOpeningAudioInfrastructure()
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

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => coordinator.StartAsync(channel));

        Assert.Contains("configured P25 key", exception.Message);

        Assert.Empty(coordinator.ActiveChannels);
        Assert.False(backend.IsDisposed);
        Assert.False(vocoder.IsDisposed);
        Assert.False(channel.CanListen);
    }

    [Fact]
    public async Task RejectsNxdnReceiveUntilAnInjectedDecoderIsAvailable()
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

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => coordinator.StartAsync(channel));

        Assert.Contains("FEC/AMBE+2 decoder", exception.Message);
        Assert.Empty(coordinator.ActiveChannels);
        Assert.False(backend.IsDisposed);
        Assert.False(vocoder.IsDisposed);
        Assert.False(channel.CanListen);
    }

    [Fact]
    public async Task RoutesNxdnReceiveThroughAnInjectedDecoderBackend()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        var nxdn = new FakeNxdnVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            createNxdnVocoderBackend: () => nxdn);
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
        Assert.Equal((short)30_000, backend.Playback.Frames[0][0]);
        Assert.Equal(1, nxdn.Session.DecodeCalls);

        await coordinator.StopAsync(channel);
        Assert.True(nxdn.IsDisposed);
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
        var keyRing = new P25KeyRing(new KeyContainer
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
    public async Task AppliesConfiguredChannelGainToSharedPlayback()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            getChannelGain: _ => 0.5);
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

        Assert.Equal((short)10_000, backend.Playback.Frames[0][0]);
    }

    [Fact]
    public async Task RoutesChannelsToSeparateConfiguredOutputDevices()
    {
        var backend = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelReceiveAudioCoordinator(
            () => backend,
            () => vocoder,
            getOutputDeviceId: channel => channel.Name == "Alternate" ? "alternate" : "output");
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

    private static FneTrafficFrame CreateTraffic(uint destinationId, byte slot)
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
            packetSequence: 1,
            streamId: 99,
            payload: new byte[DmrVoicePacketCodec.PacketBytes]);
    }

    private static FneTrafficFrame CreateAnalogTraffic(uint destinationId)
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
            packetSequence: 1,
            streamId: 99,
            payload: AnalogVoicePacketCodec.CreatePacket(AnalogAudioFrameType.Voice, 1, destinationId, samples));
    }

    private static FneTrafficFrame CreateNxdnTraffic(uint destinationId)
    {
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
            payload: new byte[NxdnVoicePacketCodec.PacketBytes]);
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

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public bool IsDisposed { get; private set; }
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public int CreateSessionCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "fake";
        public bool IsAvailable => true;

        public IVocoderSession CreateSession(VocoderMode mode)
        {
            CreateSessionCalls++;
            return new FakeVocoderSession();
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            samples.Fill(20_000);
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeNxdnVocoderBackend : INxdnVocoderBackend
    {
        public FakeNxdnVocoderSession Session { get; } = new();
        public bool IsDisposed { get; private set; }
        public string Name => "fake-nxdn";
        public bool IsAvailable => !IsDisposed;

        public INxdnVocoderSession CreateSession() => Session;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeNxdnVocoderSession : INxdnVocoderSession
    {
        public int DecodeCalls { get; private set; }

        public int Decode(ReadOnlySpan<byte> frame, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill(30_000);
            return 0;
        }

        public void Dispose()
        {
        }
    }
}
