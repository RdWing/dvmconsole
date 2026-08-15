using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.DMR;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrVoicePacketCodecTests
{
    [Fact]
    public void ExtractsThreeAmbeCodewordsFromDmrPacketLayout()
    {
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        for (int index = 0; index < DmrVoicePacketCodec.FrameBytes; index++)
            packet[DmrVoicePacketCodec.HeaderBytes + index] = (byte)index;

        byte[] ambe = DmrVoicePacketCodec.ExtractAmbe(packet);

        Assert.Equal(27, ambe.Length);
        Assert.Equal(Enumerable.Range(0, 13).Select(value => (byte)value), ambe[..13]);
        Assert.Equal((byte)3, ambe[13]);
        Assert.Equal(Enumerable.Range(20, 13).Select(value => (byte)value), ambe[14..]);
    }

    [Fact]
    public void CreatesDmrVoicePacketThatRoundTripsAmbeLayout()
    {
        byte[] ambe = Enumerable.Range(0, DmrVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)value)
            .ToArray();

        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 1,
            voiceSync: false,
            embeddedSequence: 3,
            frameSequence: 7,
            ambe);

        Assert.Equal(DmrVoicePacketCodec.PacketBytes, packet.Length);
        Assert.Equal("DMRD", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal((byte)7, packet[4]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, packet[5..8]);
        Assert.Equal(new byte[] { 0xA0, 0xB0, 0xC0 }, packet[8..11]);
        Assert.Equal((byte)0x83, packet[15]);
        Assert.Equal(ambe, DmrVoicePacketCodec.ExtractAmbe(packet));
    }

    [Fact]
    public void ExtractsProtocolEncryptionFromPrivacyIndicatorHeader()
    {
        byte[] frame = new byte[DmrVoicePacketCodec.FrameBytes];
        var privacy = new PrivacyLC
        {
            AlgId = 3,
            KId = 0x55,
            Group = true,
            DstId = 100
        };
        FullLC.EncodePI(privacy, ref frame);
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        frame.CopyTo(packet, DmrVoicePacketCodec.HeaderBytes);

        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(
            packet,
            out DmrVoicePacketCodec.DmrEncryptionMetadata metadata));
        Assert.Equal((byte)3, metadata.AlgorithmId);
        Assert.Equal((byte)0x55, metadata.KeyId);
    }

    [Theory]
    [InlineData((byte)0, (byte)0x10)]
    [InlineData((byte)1, (byte)0x90)]
    public void EncodesZeroBasedSlotInDmrNetworkHeader(byte slot, byte expectedHeader)
    {
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId: 1,
            destinationId: 2,
            slot,
            voiceSync: true,
            embeddedSequence: 0,
            frameSequence: 0,
            new byte[DmrVoicePacketCodec.AmbeBytes]);

        Assert.Equal(expectedHeader, packet[15]);
    }

    [Fact]
    public async Task DmrSessionDecodesThreeCodewordsToPlaybackFrames()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            3,
            0,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]);

        int errors = await session.ProcessAsync(traffic);

        Assert.Equal(0, errors);
        Assert.Equal(3, session.FramesDecoded);
        Assert.Equal(3, vocoder.DecodeCalls);
        Assert.Equal(3, playback.Frames.Count);
        Assert.All(playback.Frames, frame => Assert.Equal(160, frame.Length));
    }

    [Fact]
    public async Task DmrSessionIgnoresNonVoiceFrames()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new DmrRxAudioSession(vocoder, playback);
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            3,
            0,
            "GROUP",
            "TERMINATOR",
            "TERMINATOR",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]);

        int errors = await session.ProcessAsync(traffic);

        Assert.Equal(0, errors);
        Assert.Equal(0, session.FramesDecoded);
        Assert.Equal(0, vocoder.DecodeCalls);
        Assert.Empty(playback.Frames);
    }

    [Fact]
    public void SelectorMatchesOnlyTheConfiguredDmrVoiceStream()
    {
        var selector = new DmrTrafficSelector(destinationId: 100, slot: 1);

        Assert.True(selector.Matches(CreateTraffic(100, 1, "VOICE")));
        Assert.True(selector.Matches(CreateTraffic(100, 1, "VOICE_SYNC")));
        Assert.False(selector.Matches(CreateTraffic(101, 1, "VOICE")));
        Assert.False(selector.Matches(CreateTraffic(100, 0, "VOICE")));
        Assert.False(selector.Matches(CreateTraffic(100, 1, "TERMINATOR")));
    }

    [Fact]
    public async Task RouterDecodesOnlySelectedTraffic()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var router = new DmrRxAudioRouter(new DmrTrafficSelector(100, 1), vocoder, playback);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(101, 1, "VOICE")));
        Assert.Equal(0, router.FramesDecoded);

        Assert.Equal(0, await router.ProcessAsync(CreateTraffic(100, 1, "VOICE")));
        Assert.Equal(3, router.FramesDecoded);
        Assert.Equal(3, playback.Frames.Count);
    }

    private static FneTrafficFrame CreateTraffic(uint destinationId, byte slot, string frameType)
    {
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            destinationId,
            slot,
            "GROUP",
            frameType,
            frameType,
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => codeword.Length;

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill((short)DecodeCalls);
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
