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
    public async Task PatchDecodeObserverReceivesProcessedTrafficIdentity()
    {
        var observed = new List<(uint StreamId, uint SourceId)>();
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
            (_, streamId, sourceId, _) => observed.Add((streamId, sourceId)),
            () => new FakeVocoderBackend());

        await coordinator.ApplyChannelsAsync([channel]);
        await coordinator.ProcessAsync(channel, CreateDmrTraffic());

        Assert.NotEmpty(observed);
        Assert.All(observed, identity => Assert.Equal(((uint)99, (uint)2), identity));
    }

    [Fact]
    public async Task TracksOnlyTheSourceStreamCurrentlyOwnedByEachDecoder()
    {
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
            (_, _) => { },
            () => new FakeVocoderBackend());
        await coordinator.ApplyChannelsAsync([channel]);

        Assert.False(coordinator.IsTrackingStream(channel, 99));
        await coordinator.ProcessAsync(channel, CreateDmrTraffic());
        Assert.True(coordinator.IsTrackingStream(channel, 99));
        Assert.False(coordinator.IsTrackingStream(channel, 100));

        await coordinator.ProcessAsync(
            channel,
            CreateDmrTerminator(destinationId: 100, streamId: 99));

        Assert.False(coordinator.IsTrackingStream(channel, 99));
    }

    [Fact]
    public async Task TerminatorRoutingSelectsOnlyThePatchDecoderThatOwnsTheStream()
    {
        ChannelViewModel first = DmrChannel("First", destinationId: 100);
        ChannelViewModel second = DmrChannel("Second", destinationId: 200);
        await using var coordinator = new PatchSourceDecodeCoordinator(
            null,
            (_, _) => { },
            () => new FakeVocoderBackend());
        await coordinator.ApplyChannelsAsync([first, second]);
        await coordinator.ProcessAsync(first, CreateDmrTraffic(destinationId: 100, streamId: 99));
        await coordinator.ProcessAsync(second, CreateDmrTraffic(destinationId: 200, streamId: 199));
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [first],
            [(FneTrafficProtocol.Dmr, 200)] = [second]
        };
        FneTrafficFrame terminator = CreateDmrTerminator(destinationId: 100, streamId: 99);

        ReceiveIngressRoutingDecision ingress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            terminator,
            coordinator.IsTrackingStream);
        ChannelViewModel target = Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            coordinator.ActiveChannels,
            terminator,
            ingress,
            coordinator.IsTrackingStream));

        Assert.Same(first, target);
        await coordinator.ProcessAsync(target, terminator);
        Assert.False(coordinator.IsTrackingStream(first, 99));
        Assert.True(coordinator.IsTrackingStream(second, 199));
    }

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
    public async Task EnablesClearNxdnAndKeepsUnresolvedEncryptedSourcesInactive()
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

        Assert.True(coordinator.IsActive(nxdn));
        Assert.False(coordinator.IsActive(encryptedP25));
        Assert.Equal(1, vocoder.CreateSessionCalls);
    }

    [Fact]
    public async Task EnablesEncryptedDmrAndNxdnSourcesWhenLocalKeysResolve()
    {
        using var dmrKeys = new DmrKeyRing("System 1", new KeyContainer
        {
            Keys = [new KeyEntry { Protocol = "dmr", AlgId = 1, KeyId = 2, Key = "0102030405" }]
        });
        using var nxdnKeys = new NxdnKeyRing("System 1", new KeyContainer
        {
            Keys = [new KeyEntry { Protocol = "nxdn", AlgId = 1, KeyId = 3, Key = "1234" }]
        });
        var dmr = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "DMR privacy",
            System = "System 1",
            Tgid = "100",
            Mode = "dmr",
            Slot = 1,
            Algo = "arc4",
            KeyId = "2"
        });
        var nxdn = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "NXDN privacy",
            System = "System 1",
            Tgid = "101",
            Mode = "nxdn",
            Algo = "ehr",
            KeyId = "3"
        });
        await using var coordinator = new PatchSourceDecodeCoordinator(
            null,
            (_, _) => { },
            () => new FakeVocoderBackend(),
            dmrKeys,
            nxdnKeys);

        await coordinator.ApplyChannelsAsync([dmr, nxdn]);

        Assert.True(coordinator.IsActive(dmr));
        Assert.True(coordinator.IsActive(nxdn));
    }

    private static ChannelViewModel DmrChannel(string name, uint destinationId)
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "System 1",
            Tgid = destinationId.ToString(),
            Mode = "dmr",
            Slot = 1
        });

    private static FneTrafficFrame CreateDmrTraffic(
        uint destinationId = 100,
        uint streamId = 99)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot: 0,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence: 1,
            streamId,
            payload: new byte[DmrVoicePacketCodec.PacketBytes]);
    }

    private static FneTrafficFrame CreateDmrTerminator(uint destinationId, uint streamId)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId,
            slot: 0,
            callType: "GROUP",
            frameType: "TERMINATOR",
            subtype: "TERMINATOR_WITH_LC",
            packetSequence: 2,
            streamId,
            payload: new byte[DmrVoicePacketCodec.PacketBytes]);

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

    private sealed class FakeVocoderSession : IHalfRateVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            samples.Fill(20_000);
            return 0;
        }

        public int FlushEncode(Span<byte> codeword) => 0;
        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters) => 0;
        public int DecodeParameters(ReadOnlySpan<byte> parameters, Span<short> samples, uint correctedErrors = 0, bool lost = false) => 0;
        public int FlushEncodeParameters(Span<byte> parameters) => 0;
        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            codeword[..parameters.Length].CopyTo(parameters);
            return parameters.Length;
        }
        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
        {
            codeword.Clear();
            parameters.CopyTo(codeword);
        }

        public void Dispose()
        {
        }
    }
}
