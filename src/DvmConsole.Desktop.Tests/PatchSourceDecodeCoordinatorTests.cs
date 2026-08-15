using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchSourceDecodeCoordinatorTests
{
    [Fact]
    public async Task DecodesEnabledDmrSourceWithoutOpeningAnAudioBackend()
    {
        var vocoder = new FakeVocoderBackend();
        List<short[]> frames = [];
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1
        });
        await using var coordinator = new PatchSourceDecodeCoordinator(
            null,
            (_, samples) => frames.Add(samples.ToArray()),
            () => vocoder);

        await coordinator.ApplyChannelsAsync([channel]);

        Assert.True(coordinator.IsActive(channel));
        Assert.True(channel.TryApplyTraffic("System 1", CreateDmrTraffic()));
        Assert.Equal(0, await coordinator.ProcessAsync(channel, CreateDmrTraffic()));
        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.Equal(160, frame.Length);
            Assert.Equal((short)20_000, frame[0]);
        });

        await coordinator.StopAllAsync();
        Assert.False(coordinator.IsActive(channel));
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task KeepsUnsupportedAndUnresolvedSourcesInactive()
    {
        var vocoder = new FakeVocoderBackend();
        var nxdn = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "NXDN",
            System = "System 1",
            Tgid = "101",
            Mode = "nxdn"
        });
        var encryptedP25 = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "102",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        });
        await using var coordinator = new PatchSourceDecodeCoordinator(
            null,
            (_, _) => { },
            () => vocoder);

        await coordinator.ApplyChannelsAsync([nxdn, encryptedP25]);

        Assert.False(coordinator.IsActive(nxdn));
        Assert.False(coordinator.IsActive(encryptedP25));
        Assert.Equal(0, vocoder.CreateSessionCalls);
    }

    private static FneTrafficFrame CreateDmrTraffic()
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: 0,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence: 1,
            streamId: 99,
            payload: new byte[DmrVoicePacketCodec.PacketBytes]);
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
}
