using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class ChannelReceiveAudioSessionTests
{
    [Fact]
    public async Task RoutesMatchingDmrTrafficThroughTheConfiguredSession()
    {
        var definition = new ChannelRuntimeDefinition("Dispatch", "System 1", "dmr", 100, 1);
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, vocoder, playback);

        int errors = await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]));

        Assert.Equal(0, errors);
        Assert.Equal(3, session.FramesDecoded);
        Assert.Equal(3, vocoder.DecodeCalls);
        Assert.Equal(3, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableDmrChannelDecodesClearReceiveWithoutPrivacyHeader()
    {
        var definition = new ChannelRuntimeDefinition(
            "Selectable",
            "System 1",
            "dmr",
            100,
            1,
            encryptionAlgorithm: "aes",
            encryptionKeyId: "0x45",
            selectableEncryption: true);
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, vocoder, playback);

        byte[] clearHeader = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId: 1,
            destinationId: 100,
            slot: 1,
            frameSequence: 0,
            encrypted: false);
        await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "DATA_SYNC",
            "VOICE_LC_HEADER",
            0,
            99,
            clearHeader));

        int errors = await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]));

        Assert.Equal(0, errors);
        Assert.Equal(3, session.FramesDecoded);
        Assert.Equal(0, session.MalformedPackets);
        Assert.Equal(3, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableSecureDmrChannelRejectsClearReceive()
    {
        var definition = new ChannelRuntimeDefinition(
            "Selectable",
            "System 1",
            "dmr",
            100,
            1,
            encryptionAlgorithm: "aes",
            encryptionKeyId: "0x45",
            selectableEncryption: true);
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(
            definition,
            new FakeVocoderSession(),
            playback,
            privacyPolicy: new FixedPrivacyPolicy(requireEncryptedTraffic: true));

        await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "DATA_SYNC",
            "VOICE_LC_HEADER",
            0,
            99,
            DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(1, 100, 1, 0, encrypted: false)));
        await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]));

        Assert.Empty(playback.Frames);
        Assert.Equal(0, session.FramesDecoded);
    }

    [Fact]
    public async Task RoutesMatchingP25TrafficThroughTheConfiguredSession()
    {
        var definition = new ChannelRuntimeDefinition("P25", "System 1", "p25", 100, 0);
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, vocoder, playback);

        int errors = await session.ProcessAsync(CreateP25Traffic());

        Assert.Equal(0, errors);
        Assert.Equal(9, session.FramesDecoded);
        Assert.Equal(9, vocoder.DecodeCalls);
        Assert.Equal(9, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableSecureP25ChannelRejectsClearReceive()
    {
        var definition = new ChannelRuntimeDefinition(
            "Selectable P25",
            "System 1",
            "p25",
            100,
            0,
            encryptionAlgorithm: "aes",
            encryptionKeyId: "0x45",
            selectableEncryption: true);
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(
            definition,
            new FakeVocoderSession(),
            playback,
            privacyPolicy: new FixedPrivacyPolicy(requireEncryptedTraffic: true));

        await session.ProcessAsync(CreateP25Traffic());

        Assert.Empty(playback.Frames);
        Assert.Equal(0, session.FramesDecoded);
    }

    [Fact]
    public async Task SelectableNxdnChannelDecodesClearReceiveWithoutPrivacyIv()
    {
        var definition = new ChannelRuntimeDefinition(
            "Selectable NXDN",
            "System 1",
            "nxdn",
            100,
            0,
            encryptionAlgorithm: "aes",
            encryptionKeyId: "0x05",
            selectableEncryption: true);
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, vocoder, playback);

        await session.ProcessAsync(CreateNxdnTraffic(
            NxdnVoicePacketCodec.CreateCallControlPacket(
                1,
                100,
                true,
                NxdnVoicePacketCodec.VoiceCallMessageType,
                0,
                cipherType: 0,
                keyId: 0),
            packetSequence: 0));
        byte[] clearVoice = NxdnVoicePacketCodec.CreateVoicePacket(
            1,
            100,
            true,
            1,
            new byte[NxdnVoicePacketCodec.AmbeBytes]);
        Assert.False(NxdnVoicePacketCodec.TryExtractCallMetadata(clearVoice, out _));
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(
            clearVoice,
            new byte[NxdnVoicePacketCodec.AmbeBytes],
            out int codewordCount));
        Assert.Equal(NxdnVoicePacketCodec.CodewordsPerFrame, codewordCount);
        int errors = await session.ProcessAsync(CreateNxdnTraffic(clearVoice, packetSequence: 1));

        Assert.Equal(0, errors);
        Assert.Equal(0, session.DuplicateOrLatePackets);
        Assert.Equal(0, session.LostPackets);
        Assert.Equal(0, session.MalformedPackets);
        Assert.Equal(NxdnVoicePacketCodec.CodewordsPerFrame, session.FramesDecoded);
        Assert.Equal(NxdnVoicePacketCodec.CodewordsPerFrame, playback.Frames.Count);
    }

    [Fact]
    public async Task SelectableSecureNxdnChannelRejectsClearReceive()
    {
        var definition = new ChannelRuntimeDefinition(
            "Selectable NXDN",
            "System 1",
            "nxdn",
            100,
            0,
            encryptionAlgorithm: "aes",
            encryptionKeyId: "0x05",
            selectableEncryption: true);
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(
            definition,
            new FakeVocoderSession(),
            playback,
            privacyPolicy: new FixedPrivacyPolicy(requireEncryptedTraffic: true));

        await session.ProcessAsync(CreateNxdnTraffic(
            NxdnVoicePacketCodec.CreateCallControlPacket(
                1,
                100,
                true,
                NxdnVoicePacketCodec.VoiceCallMessageType,
                0,
                cipherType: 0,
                keyId: 0),
            packetSequence: 0));
        await session.ProcessAsync(CreateNxdnTraffic(
            NxdnVoicePacketCodec.CreateVoicePacket(
                1,
                100,
                true,
                1,
                new byte[NxdnVoicePacketCodec.AmbeBytes]),
            packetSequence: 1));

        Assert.Empty(playback.Frames);
        Assert.Equal(0, session.FramesDecoded);
    }

    [Fact]
    public async Task RoutesMatchingAnalogPcmThroughTheConfiguredSession()
    {
        var definition = new ChannelRuntimeDefinition("Analog", "System 1", "analog", 100, 0);
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, null, playback);

        int errors = await session.ProcessAsync(CreateAnalogTraffic());

        Assert.Equal(0, errors);
        Assert.Equal(1, session.FramesDecoded);
        Assert.Single(playback.Frames);
        Assert.Equal(
            AnalogVoicePacketCodec.DecodeMuLaw(AnalogVoicePacketCodec.EncodeMuLaw(-80)),
            playback.Frames[0][0]);
        Assert.Equal(
            AnalogVoicePacketCodec.DecodeMuLaw(AnalogVoicePacketCodec.EncodeMuLaw(79)),
            playback.Frames[0][159]);
    }

    [Fact]
    public async Task DropsMalformedAnalogFramesAndRecoversOnTheNextFrame()
    {
        var definition = new ChannelRuntimeDefinition("Analog", "System 1", "analog", 100, 0);
        var playback = new FakePlayback();
        await using var session = new ChannelReceiveAudioSession(definition, null, playback);

        Assert.Equal(0, await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Analog,
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[10])));
        Assert.Equal(1, session.MalformedPackets);
        Assert.Empty(playback.Frames);

        Assert.Equal(0, await session.ProcessAsync(CreateAnalogTraffic()));
        Assert.Equal(1, session.FramesDecoded);
        Assert.Single(playback.Frames);
    }

    [Fact]
    public async Task DropsMalformedNxdnFramesAndRecoversOnTheNextFrame()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        var definition = new ChannelRuntimeDefinition("NXDN", "System 1", "nxdn", 100, 0);
        await using var session = new ChannelReceiveAudioSession(
            definition,
            vocoder,
            playback);

        Assert.Equal(0, await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Nxdn,
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            "VCALL",
            1,
            99,
            new byte[10])));
        Assert.Equal(1, session.MalformedPackets);
        Assert.Empty(playback.Frames);

        Assert.Equal(0, await session.ProcessAsync(CreateNxdnTraffic()));
        Assert.Equal(4, session.FramesDecoded);
        Assert.Equal(4, playback.Frames.Count);
    }

    [Fact]
    public void ReceiveDiagnosticsSummarizeQualityCounters()
    {
        var clean = new ReceiveAudioDiagnostics(9, 0, 0, 0);
        Assert.False(clean.HasIssues);
        Assert.Equal("9 decoded frames", clean.SummaryText);

        var degraded = new ReceiveAudioDiagnostics(90, 2, 1, 3);
        Assert.True(degraded.HasIssues);
        Assert.Equal("lost 2, late/duplicate 1, malformed 3", degraded.SummaryText);
    }

    [Fact]
    public void RequiresTheMandatoryVocoderForNxdnReceive()
    {
        var definition = new ChannelRuntimeDefinition("NXDN", "System 1", "nxdn", 100, 0);

        Assert.Throws<ArgumentNullException>(() => new ChannelReceiveAudioSession(
            definition,
            vocoder: null,
            new FakePlayback()));
    }

    [Fact]
    public async Task RoutesAllFourNxdnCodewordsThroughTheMandatoryVocoder()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        var definition = new ChannelRuntimeDefinition("NXDN", "System 1", "nxdn", 100, 0);
        await using var session = new ChannelReceiveAudioSession(
            definition,
            vocoder,
            playback);

        int errors = await session.ProcessAsync(CreateNxdnTraffic());

        Assert.Equal(0, errors);
        Assert.Equal(4, session.FramesDecoded);
        Assert.Equal(4, vocoder.DecodeCalls);
        Assert.Equal(4, playback.Frames.Count);
        Assert.Equal(160, playback.Frames[0].Length);
        Assert.Equal((short)1, playback.Frames[0][0]);
    }

    [Fact]
    public async Task IgnoresTrafficForAnotherChannel()
    {
        var definition = new ChannelRuntimeDefinition("Dispatch", "System 1", "dmr", 100, 1);
        await using var session = new ChannelReceiveAudioSession(
            definition,
            new FakeVocoderSession(),
            new FakePlayback());

        int errors = await session.ProcessAsync(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            101,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            new byte[DmrVoicePacketCodec.PacketBytes]));

        Assert.Equal(0, errors);
        Assert.Equal(0, session.FramesDecoded);
    }

    private static FneTrafficFrame CreateP25Traffic()
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
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            "LDU1",
            1,
            99,
            payload);
    }

    private sealed class FixedPrivacyPolicy(bool requireEncryptedTraffic) : IReceivePrivacyPolicy
    {
        public bool RequireEncryptedTraffic { get; } = requireEncryptedTraffic;
    }

    private static FneTrafficFrame CreateAnalogTraffic()
    {
        var samples = new short[AnalogVoicePacketCodec.SamplesPerPacket];
        for (int index = 0; index < AnalogVoicePacketCodec.SamplesPerPacket; index++)
            samples[index] = (short)(index - 80);

        return new FneTrafficFrame(
            FneTrafficProtocol.Analog,
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            "VOICE",
            1,
            99,
            AnalogVoicePacketCodec.CreatePacket(AnalogAudioFrameType.Voice, 1, 100, samples));
    }

    private static FneTrafficFrame CreateNxdnTraffic(byte[]? payload = null, ushort packetSequence = 2)
    {
        if (payload is null)
        {
            byte[] ambe = Enumerable.Range(1, NxdnVoicePacketCodec.AmbeBytes)
                .Select(value => (byte)value)
                .ToArray();
            payload = NxdnVoicePacketCodec.CreateVoicePacket(1, 100, true, 0, ambe);
        }

        return new FneTrafficFrame(
            FneTrafficProtocol.Nxdn,
            1,
            2,
            100,
            null,
            "GROUP",
            "VOICE",
            "VCALL",
            packetSequence,
            99,
            payload);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DecodeCalls { get; private set; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) => 0;

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
