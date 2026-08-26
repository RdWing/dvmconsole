using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.DMR;
using Xunit;

namespace DvmConsole.Media.Tests;

[Collection("DMR wire codec")]
public sealed class DmrTxCallSessionTests
{
    [Fact]
    public void EmitsVoiceHeaderVoicePacketAndTerminatorForOneCall()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new DmrTxCallSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            slot: 0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)));

        session.Start();
        var headerPacket = Assert.Single(packets);
        Assert.Equal((ushort)0, headerPacket.Sequence);
        Assert.Equal((uint)77, headerPacket.Stream);
        Assert.Equal((byte)0x21, headerPacket.Payload[15]);

        LC headerLc = Assert.IsType<LC>(FullLC.Decode(
            headerPacket.Payload[20..],
            DMRDataType.VOICE_LC_HEADER));
        Assert.Equal((uint)0x010203, headerLc.SrcId);
        Assert.Equal((uint)0xA0B0C0, headerLc.DstId);

        Assert.Equal(1, session.Process(new short[480]));
        session.End();

        Assert.Equal(8, packets.Count);
        Assert.Equal((ushort)1, packets[1].Sequence);
        Assert.Equal((byte)0x10, packets[1].Payload[15]);
        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
            packets.Skip(2).Take(5).Select(packet => packet.Payload[15]));
        Assert.Equal((ushort)7, packets[^1].Sequence);
        Assert.Equal((byte)0x22, packets[^1].Payload[15]);
        Assert.True(session.IsEnded);
    }

    [Fact]
    public void FlushesPartialPcmAndAmbeBeforeCompletingSuperframe()
    {
        var packets = new List<byte[]>();
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        session.Start();
        Assert.Equal(0, session.Process(new short[200]));
        session.End();

        Assert.Equal(8, packets.Count);
        Assert.Equal((byte)0x10, packets[1][15]);
        Assert.Equal((byte)0x22, packets[^1][15]);
    }

    [Fact]
    public void SecureCallMarksBothHeaderAndTerminatorEncrypted()
    {
        byte[] key = Convert.FromHexString("0102030405");
        var privacy = new DmrPrivacyOptions(
            DmrPrivacyAlgorithms.Arc4,
            keyId: 7,
            key,
            Convert.FromHexString("12345678"));
        var packets = new List<byte[]>();
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeHalfRateSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()),
            privacy: privacy);

        session.Start();
        session.End();

        LC header = Assert.IsType<LC>(FullLC.Decode(
            packets[0][DmrVoicePacketCodec.HeaderBytes..],
            DMRDataType.VOICE_LC_HEADER));
        LC terminator = Assert.IsType<LC>(FullLC.Decode(
            packets[^1][DmrVoicePacketCodec.HeaderBytes..],
            DMRDataType.TERMINATOR_WITH_LC));
        Assert.True(header.Encrypted);
        Assert.True(terminator.Encrypted);
        Assert.Equal(DmrPrivacyAlgorithms.FeatureId, header.FID);
        Assert.Equal(DmrPrivacyAlgorithms.FeatureId, terminator.FID);
        Assert.Equal(0x40, header.GetBytes()[2] & 0x40);
        Assert.Equal(0x40, terminator.GetBytes()[2] & 0x40);
    }

    [Fact]
    public async Task AsyncEndPacesEveryCompletionBurstAndTerminator()
    {
        var packets = new List<byte[]>();
        var cadence = new ManualCadence();
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 0,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, _, _) => packets.Add(payload.ToArray()));

        session.Start();
        session.Process(new short[480]);
        int packetsBeforeEnd = packets.Count;
        int packetsEmittedByEnd = 8 - packetsBeforeEnd;
        ValueTask end = session.EndAsync(cadence.WaitAsync, CancellationToken.None);

        await WaitUntilAsync(() => cadence.WaitCount == 1);
        Assert.Equal(packetsBeforeEnd, packets.Count);

        for (int emitted = 1; emitted <= packetsEmittedByEnd; emitted++)
        {
            cadence.Release();
            await WaitUntilAsync(() => packets.Count == packetsBeforeEnd + emitted);
            if (emitted < packetsEmittedByEnd)
                await WaitUntilAsync(() => cadence.WaitCount == emitted + 1);
        }

        await end;

        Assert.Equal(8, packets.Count);
        Assert.Equal((byte)0x22, packets[^1][15]);
        Assert.True(session.IsEnded);
    }

    [Fact]
    public void RequiresAnExplicitStartBeforeProcessingAudio()
    {
        using var session = new DmrTxCallSession(
            sourceId: 1,
            destinationId: 2,
            slot: 1,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    [Fact]
    public void ReservesTheRtpCallEndSequenceWhenAdvancing()
    {
        var sequence = new DmrTxPacketSequence(packetSequence: 0xFFFE, frameSequence: 4);

        sequence.Advance();

        Assert.Equal((ushort)0, sequence.PacketSequence);
        Assert.Equal((byte)5, sequence.FrameSequence);
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }

    private sealed class FakeHalfRateSession : IHalfRateVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Clear();
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters)
        {
            parameters.Clear();
            return parameters.Length;
        }
        public int FlushEncodeParameters(Span<byte> parameters) => 0;
        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false) => 0;
        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            parameters.Clear();
            return parameters.Length;
        }
        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
            => codeword.Clear();
        public void Dispose() { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class ManualCadence
    {
        private readonly SemaphoreSlim releases = new(0);
        private int waitCount;

        public int WaitCount => Volatile.Read(ref waitCount);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waitCount);
            await releases.WaitAsync(cancellationToken);
        }

        public void Release() => releases.Release();
    }
}
