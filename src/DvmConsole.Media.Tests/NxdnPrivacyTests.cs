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
    [InlineData("ABCDEF1234567890", "8B8DDEEB890F4CF1")]
    [InlineData("8B8DDEEB890F4CF1", "C83AB2073A8D80E8")]
    [InlineData("C83AB2073A8D80E8", "6C4663AF3D39244B")]
    [InlineData("6C4663AF3D39244B", "00A5754C8AC49E3F")]
    [InlineData("00A5754C8AC49E3F", "81C55D6EA55913C7")]
    [InlineData("81C55D6EA55913C7", "C5522BA39D354F27")]
    [InlineData("C5522BA39D354F27", "C8065031EAF69931")]
    [InlineData("C8065031EAF69931", "03C240EC1877D693")]
    public void IvGeneratorMatchesPublishedNxdnSecurityVectors(
        string seed,
        string expectedNextSeed)
    {
        byte[] actual = NxdnInitializationVectorGenerator.GetNextSeed(
            Convert.FromHexString(seed));

        Assert.Equal(expectedNextSeed, Convert.ToHexString(actual));
    }

    [Fact]
    public void AesInitializationVectorMatchesPublishedNxdnSecurityVector()
    {
        byte[] actual = NxdnInitializationVectorGenerator.CreateAesInitializationVector(
            Convert.FromHexString("ABCDEF1234567890"));

        Assert.Equal(
            "ABCDEF12345678908B8DDEEB890F4CF1",
            Convert.ToHexString(actual));
    }

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
    public void DesCallCarriesVcallAndIvInOneStartupFrameBeforeVoice()
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

        Assert.Equal(2, packets.Count);
        Assert.True(NxdnVoicePacketCodec.TryExtractFacchCallMetadata(packets[0], 0, out var header));
        Assert.Equal(NxdnPrivacyAlgorithms.Des, header.CipherType);
        Assert.Equal((byte)5, header.KeyId);
        Assert.True(NxdnVoicePacketCodec.TryExtractFacchCallMetadata(packets[0], 1, out var iv));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallIvMessageType, iv.MessageType);
        Assert.Equal(options.MessageIndicator.ToArray(), iv.MessageIndicator);
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packets[1], new byte[NxdnVoicePacketCodec.AmbeBytes], out int count));
        Assert.Equal(4, count);
    }

    [Fact]
    public void DesCallAlternatesVcallAndSuccessorIvInSacchWithoutDroppingVoice()
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
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 40]);

        Assert.Equal(11, packets.Count);
        Assert.All(
            packets.Skip(1),
            packet => Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(
                packet,
                new byte[NxdnVoicePacketCodec.AmbeBytes],
                out int count) && count == 4));

        var collector = new NxdnSacchMessageCollector();
        NxdnVoicePacketCodec.CallMetadata voiceCall = default;
        NxdnVoicePacketCodec.CallMetadata rotatedIv = default;
        for (int index = 1; index <= 4; index++)
            collector.TryAccept(packets[index], out voiceCall);
        for (int index = 5; index <= 8; index++)
            collector.TryAccept(packets[index], out rotatedIv);

        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, voiceCall.MessageType);
        Assert.Equal(NxdnPrivacyAlgorithms.Des, voiceCall.CipherType);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallIvMessageType, rotatedIv.MessageType);
        Assert.Equal(
            NxdnInitializationVectorGenerator.GetNextSeed(options.MessageIndicator.Span),
            rotatedIv.MessageIndicator);

        Assert.False(collector.TryAccept(packets[9], out _));
        Assert.False(collector.TryAccept(packets[10], out _));
    }

    [Fact]
    public async Task AsyncEndPacesPaddedVoiceFrameAndRelease()
    {
        var packets = new List<byte[]>();
        var packetCountsAtWait = new List<int>();
        using var call = new NxdnTxCallSession(
            1001,
            2002,
            true,
            99,
            new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()));
        call.Start();
        call.Process(new short[160]);

        await call.EndAsync(
            _ =>
            {
                packetCountsAtWait.Add(packets.Count);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal([1, 2], packetCountsAtWait);
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(
            packets[1],
            new byte[NxdnVoicePacketCodec.AmbeBytes],
            out _));
        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packets[2], out var release));
        Assert.Equal(NxdnVoicePacketCodec.TransmitReleaseMessageType, release.MessageType);
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

        await session.ProcessAsync(Traffic(NxdnVoicePacketCodec.CreatePrivacyCallStartPacket(
            1001, 2002, true, 0, 2, 5, mi), 0));
        await session.ProcessAsync(Traffic(NxdnVoicePacketCodec.CreateVoicePacket(
            1001, 2002, true, 1, encrypted), 1));

        Assert.Equal(4, decoder.DecodedParameters.Count);
        byte[] expectedParameters = Enumerable.Range(0, 4)
            .SelectMany(index => clear.AsSpan(index * 9, 7).ToArray())
            .ToArray();
        Assert.Equal(expectedParameters, decoder.DecodedParameters.SelectMany(value => value).ToArray());
    }

    [Fact]
    public async Task EhrReceiveRebuildsPrivacyProcessorAtNewStreamVcall()
    {
        byte[] key = Convert.FromHexString("1234");
        var packets = new List<byte[]>();
        using (var call = new NxdnTxCallSession(
            1001,
            2002,
            true,
            99,
            new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            new NxdnPrivacyOptions(NxdnPrivacyAlgorithms.Ehr, 5, key)))
        {
            call.Start();
            call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 4]);
        }

        using var ring = new NxdnKeyRing("System A", new KeyContainer
        {
            Keys = [new KeyEntry
            {
                Protocol = "nxdn",
                AlgId = NxdnPrivacyAlgorithms.Ehr,
                KeyId = 5,
                Key = Convert.ToHexString(key)
            }]
        });
        var decoder = new FakeHalfRateSession();
        await using var receiver = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002),
            decoder,
            new DiscardPlayback(),
            ring,
            "System A",
            configuredAlgorithm: "ehr",
            configuredKeyId: "5");

        await receiver.ProcessAsync(Traffic(packets[0], 0));
        await receiver.ProcessAsync(Traffic(packets[1], 1));

        Assert.Equal(4, receiver.FramesDecoded);
        Assert.Equal(4, decoder.DecodedParameters.Count);
        Assert.Equal(0, receiver.MalformedPackets);
    }

    [Fact]
    public async Task ReceiveUsesSacchSuccessorIvForLateEntryWithoutStandaloneFacch()
    {
        byte[] key = Convert.FromHexString("133457799BBCDFF1");
        byte[] initialMi = Convert.FromHexString("0123456789ABCDEF");
        var options = new NxdnPrivacyOptions(NxdnPrivacyAlgorithms.Des, 5, key, initialMi);
        var packets = new List<byte[]>();
        using (var call = new NxdnTxCallSession(
            1001,
            2002,
            true,
            99,
            new FakeHalfRateSession(),
            (payload, _, _) => packets.Add(payload.ToArray()),
            options))
        {
            call.Start();
            call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * 48]);
        }

        using var ring = new NxdnKeyRing("System A", new KeyContainer
        {
            Keys = [new KeyEntry
            {
                Protocol = "nxdn",
                AlgId = NxdnPrivacyAlgorithms.Des,
                KeyId = 5,
                Key = Convert.ToHexString(key)
            }]
        });
        var decoder = new FakeHalfRateSession();
        await using var receiver = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002),
            decoder,
            new DiscardPlayback(),
            ring,
            "System A",
            configuredAlgorithm: "des",
            configuredKeyId: "5");

        // Skip startup and the first four VCALL voice frames. The next four
        // frames carry VCALL_IV in SACCH while still using the previous IV;
        // the following four must decode with the advertised successor.
        byte[][] lateEntryPackets = packets.Skip(5).Take(8).ToArray();
        for (int index = 0; index < lateEntryPackets.Length; index++)
            await receiver.ProcessAsync(Traffic(lateEntryPackets[index], (ushort)index));

        Assert.Equal(32, receiver.FramesDecoded);
        Assert.Equal(16, decoder.DecodedParameters.Count);
        Assert.All(
            decoder.DecodedParameters,
            parameters => Assert.All(parameters, value => Assert.Equal((byte)0, value)));
        Assert.Equal(4, receiver.MalformedPackets);
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
