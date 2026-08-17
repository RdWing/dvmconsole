using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class PatchTransmitSessionTests
{
    [Fact]
    public void SelectsAnalogPatchLifecycleWithoutAocoder()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("Analog", "Beta", "analog", 200, 0),
            sourceId: 42,
            streamId: 77,
            vocoder: null,
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        session.Start();
        Assert.Equal(1, session.Process(new short[160]));
        session.End();

        Assert.Equal(2, packets.Count);
        Assert.Equal(AnalogAudioFrameType.VoiceStart, (AnalogAudioFrameType)packets[0].Payload[15]);
        Assert.Equal(AnalogAudioFrameType.Terminator, (AnalogAudioFrameType)packets[1].Payload[15]);
    }

    [Fact]
    public void SelectsDmrAndNxdnPatchLifecycles()
    {
        var packets = new List<byte[]>();
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "Beta", "dmr", 200, 1),
            sourceId: 42,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        session.Start();
        Assert.Equal(1, session.Process(new short[480]));
        session.End();

        Assert.Equal(8, packets.Count);

        packets.Clear();
        using var nxdn = new PatchTransmitSession(
            new ChannelRuntimeDefinition("NXDN", "Beta", "nxdn", 200, 0),
            42,
            78,
            new FakeVocoderSession(),
            (payload, _, _) => packets.Add(payload.ToArray()));
        nxdn.Start();
        Assert.Equal(1, nxdn.Process(new short[640]));
        nxdn.End();

        Assert.Equal(3, packets.Count);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, packets[0][4]);
        Assert.Equal(NxdnVoicePacketCodec.TransmitReleaseMessageType, packets[2][4]);
        Assert.Throws<InvalidOperationException>(() => new PatchTransmitSession(
            new ChannelRuntimeDefinition("RX", "Beta", "analog", 201, 0, rxOnly: true),
            42,
            79,
            null,
            (_, _, _) => { }));
    }

    [Fact]
    public void RequiresStartBeforePatchAudio()
    {
        using var session = new PatchTransmitSession(
            new ChannelRuntimeDefinition("P25", "Beta", "p25", 200, 0),
            42,
            77,
            new FakeVocoderSession(),
            (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    [Fact]
    public void EncryptedDmrAndNxdnPatchTargetsUseTheirPrivacyHeaders()
    {
        var dmrPackets = new List<byte[]>();
        using var dmr = new PatchTransmitSession(
            new ChannelRuntimeDefinition("DMR", "Beta", "dmr", 200, 0, encryptionAlgorithm: "arc4", encryptionKeyId: "1"),
            42,
            77,
            new FakeVocoderSession(),
            (payload, _, _) => dmrPackets.Add(payload.ToArray()),
            dmrPrivacy: new DmrPrivacyOptions(
                DmrPrivacyAlgorithms.Arc4,
                1,
                Convert.FromHexString("0102030405"),
                Convert.FromHexString("12345678")));
        dmr.Start();

        Assert.True(DmrVoicePacketCodec.TryExtractEncryptionMetadata(dmrPackets[1], out var dmrMetadata));
        Assert.Equal(DmrPrivacyAlgorithms.Arc4, dmrMetadata.AlgorithmId);

        var nxdnPackets = new List<byte[]>();
        using var nxdn = new PatchTransmitSession(
            new ChannelRuntimeDefinition("NXDN", "Beta", "nxdn", 200, 0, encryptionAlgorithm: "ehr", encryptionKeyId: "3"),
            42,
            78,
            new FakeVocoderSession(),
            (payload, _, _) => nxdnPackets.Add(payload.ToArray()),
            nxdnPrivacy: new NxdnPrivacyOptions(
                NxdnPrivacyAlgorithms.Ehr,
                3,
                Convert.FromHexString("1234")));
        nxdn.Start();

        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(nxdnPackets[0], out var nxdnMetadata));
        Assert.Equal(NxdnPrivacyAlgorithms.Ehr, nxdnMetadata.CipherType);
        Assert.Equal((byte)3, nxdnMetadata.KeyId);
    }

    private sealed class FakeVocoderSession : IHalfRateVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
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
