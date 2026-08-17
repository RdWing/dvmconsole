using DvmConsole.Core.Configuration;
using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class NxdnPrivacyTests
{
    public static TheoryData<byte, byte[], byte[]> Algorithms => new()
    {
        { NxdnPrivacyAlgorithms.Ehr, Convert.FromHexString("1234"), [] },
        { NxdnPrivacyAlgorithms.Des, Convert.FromHexString("133457799BBCDFF1"), Convert.FromHexString("0123456789ABCDEF") },
        { NxdnPrivacyAlgorithms.Aes256, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(), Convert.FromHexString("0123456789ABCDEF") }
    };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void PrivacyTransformRoundTripsNaturalAmbeParameters(byte algorithm, byte[] key, byte[] mi)
    {
        var options = new NxdnPrivacyOptions(algorithm, 7, key, mi);
        using var encryptor = new NxdnPrivacyProcessor(new FakeHalfRateSession(), options);
        using var decryptor = new NxdnPrivacyProcessor(new FakeHalfRateSession(), options);

        for (int index = 0; index < 40; index++)
        {
            byte[] clear = [1, 2, 3, 4, 5, (byte)index, 0x80];
            byte[] transformed = clear.ToArray();
            encryptor.ProcessParameters(transformed);
            Assert.NotEqual(clear, transformed);
            decryptor.ProcessParameters(transformed);
            Assert.Equal(clear, transformed);
        }
    }

    [Fact]
    public void DesCallCarriesCipherAndIvBeforeVoice()
    {
        var packets = new List<byte[]>();
        var options = new NxdnPrivacyOptions(
            NxdnPrivacyAlgorithms.Des,
            5,
            Convert.FromHexString("133457799BBCDFF1"),
            Convert.FromHexString("0123456789ABCDEF"));
        using var call = new NxdnTxCallSession(
            1001, 2002, true, 99, new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            options);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 4]);

        Assert.Equal(3, packets.Count);
        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packets[0], out var header));
        Assert.Equal(NxdnPrivacyAlgorithms.Des, header.CipherType);
        Assert.Equal((byte)5, header.KeyId);
        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packets[1], out var iv));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallIvMessageType, iv.MessageType);
        Assert.Equal(options.MessageIndicator.ToArray(), iv.MessageIndicator);
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packets[2], new byte[NxdnVoicePacketCodec.AmbeBytes], out int count));
        Assert.Equal(4, count);
    }

    [Fact]
    public void DesCallRotatesMessageIndicatorBeforeNinthVoiceFrame()
    {
        var packets = new List<byte[]>();
        var options = new NxdnPrivacyOptions(
            NxdnPrivacyAlgorithms.Des,
            5,
            Convert.FromHexString("133457799BBCDFF1"),
            Convert.FromHexString("0123456789ABCDEF"));
        using var call = new NxdnTxCallSession(
            1001, 2002, true, 99, new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            options);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 36]);

        Assert.Equal(12, packets.Count);
        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packets[10], out var rotatedIv));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallIvMessageType, rotatedIv.MessageType);
        Assert.NotEqual(options.MessageIndicator.ToArray(), rotatedIv.MessageIndicator);
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packets[11], new byte[NxdnVoicePacketCodec.AmbeBytes], out int count));
        Assert.Equal(4, count);
    }

    [Fact]
    public void KeyRingLoadsOnlySystemScopedNxdnKeys()
    {
        using var ring = new NxdnKeyRing("System A", new KeyContainer
        {
            Keys =
            [
                new KeyEntry { Protocol = "nxdn", AlgId = 1, KeyId = 3, Key = "1234" },
                new KeyEntry { Protocol = "dmr", AlgId = 1, KeyId = 3, Key = "0102030405" }
            ]
        });

        Assert.True(ring.CanResolve("system a", "ehr", "3"));
        Assert.False(ring.CanResolve("System B", "ehr", "3"));
        Assert.False(ring.CanResolve("System A", "des", "3"));
    }

    [Fact]
    public async Task ReceiveUsesVcallAndIvMetadataBeforeDecryptingVoice()
    {
        byte[] key = Convert.FromHexString("133457799BBCDFF1");
        byte[] mi = Convert.FromHexString("0123456789ABCDEF");
        var options = new NxdnPrivacyOptions(NxdnPrivacyAlgorithms.Des, 5, key, mi);
        byte[] clear = new byte[NxdnVoicePacketCodec.AmbeBytes];
        for (int codeword = 0; codeword < 4; codeword++)
        {
            for (int index = 0; index < 6; index++)
                clear[(codeword * 9) + index] = (byte)(1 + codeword + index);
            clear[(codeword * 9) + 6] = 0x80;
        }
        byte[] encrypted = new byte[clear.Length];
        using (var encryptor = new NxdnPrivacyProcessor(new FakeHalfRateSession(), options))
        {
            for (int index = 0; index < 4; index++)
            {
                encryptor.ProcessCodeword(
                    clear.AsSpan(index * 9, 9),
                    encrypted.AsSpan(index * 9, 9));
            }
        }

        using var ring = new NxdnKeyRing("System A", new KeyContainer
        {
            Keys = [new KeyEntry { Protocol = "nxdn", AlgId = 2, KeyId = 5, Key = Convert.ToHexString(key) }]
        });
        var decoder = new FakeHalfRateSession();
        await using var session = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002), decoder, new DiscardPlayback(), ring, "System A");

        await session.ProcessAsync(Traffic(NxdnVoicePacketCodec.CreateCallControlPacket(
            1001, 2002, true, NxdnVoicePacketCodec.VoiceCallMessageType, 0, 2, 5), 0));
        await session.ProcessAsync(Traffic(NxdnVoicePacketCodec.CreateCallControlPacket(
            1001, 2002, true, NxdnVoicePacketCodec.VoiceCallIvMessageType, 1, messageIndicator: mi), 1));
        await session.ProcessAsync(Traffic(NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 2, encrypted), 2));

        Assert.Equal(4, decoder.DecodedParameters.Count);
        byte[] expectedParameters = Enumerable.Range(0, 4)
            .SelectMany(index => clear.AsSpan(index * 9, 7).ToArray())
            .ToArray();
        Assert.Equal(expectedParameters, decoder.DecodedParameters.SelectMany(value => value).ToArray());
    }

    private static FneTrafficFrame Traffic(byte[] payload, ushort sequence) => new(
        FneTrafficProtocol.Nxdn, 1, 1001, 2002, null, "GROUP", "VOICE", "VCALL", sequence, 99, payload);

    private sealed class FakeHalfRateSession : IHalfRateVocoderSession
    {
        public List<byte[]> DecodedCodewords { get; } = [];
        public List<byte[]> DecodedParameters { get; } = [];
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodedCodewords.Add(codeword.ToArray());
            return 0;
        }
        public int FlushEncode(Span<byte> codeword) => 0;
        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters) => 0;
        public int DecodeParameters(ReadOnlySpan<byte> parameters, Span<short> samples, uint correctedErrors = 0, bool lost = false)
        {
            DecodedParameters.Add(parameters.ToArray());
            return 0;
        }
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
        public void Dispose() { }
    }

    private sealed class DiscardPlayback : IAudioPlayback
    {
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
