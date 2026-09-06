// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25TxCallSessionTests
{
    [Fact]
    public async Task EmitsGrantDemandVoiceLdUsAndOneTerminatorTdu()
    {
        var packets = new List<(byte[] Payload, ushort Sequence, uint Stream)>();
        using var session = new P25TxCallSession(
            sourceId: 0x010203,
            destinationId: 0xA0B0C0,
            streamId: 77,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, stream) => packets.Add((payload.ToArray(), sequence, stream)),
            waitForNextPacket: TestPacketCadence.NoDelayAsync);

        session.Start();

        Assert.Single(packets);
        Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packets[0].Sequence);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[0].Payload[22]);
        Assert.Equal((byte)0x80, packets[0].Payload[14]);

        Assert.Equal(2, session.Process(new short[18 * 160]));
        await session.EndAsync();

        Assert.Equal(4, packets.Count);
        Assert.Equal((ushort)0, packets[1].Sequence);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[1].Payload[22]);
        Assert.Equal((ushort)1, packets[2].Sequence);
        Assert.Equal(P25DfsiFrameCodec.Ldu2Duid, packets[2].Payload[22]);
        Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packets[3].Sequence);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[3].Payload[22]);
        Assert.Equal((byte)0, packets[3].Payload[14]);
        Assert.True(session.IsEnded);
        Assert.Equal(18, session.CodewordsEncoded);
        Assert.Equal(2, session.LdusSent);
    }

    [Fact]
    public void RequiresAnExplicitStartBeforeProcessingAudio()
    {
        using var session = new P25TxCallSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (_, _, _) => { });

        Assert.Throws<InvalidOperationException>(() => session.Process(new short[160]));
    }

    [Fact]
    public async Task PadsPartialAudioIntoAnLduBeforeSendingTerminators()
    {
        var packets = new List<(byte[] Payload, ushort Sequence)>();
        using var session = new P25TxCallSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, _) => packets.Add((payload.ToArray(), sequence)),
            waitForNextPacket: TestPacketCadence.NoDelayAsync);

        session.Start();
        Assert.Equal(0, session.Process(new short[161]));

        await session.EndAsync();

        Assert.Equal(3, packets.Count);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[1].Payload[22]);
        Assert.Equal((ushort)0, packets[1].Sequence);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[2].Payload[22]);
        Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packets[2].Sequence);
        Assert.Equal(9, session.CodewordsEncoded);
        Assert.Equal(1, session.LdusSent);
    }

    [Fact]
    public async Task AsyncEndPacesPaddedLduAndTerminator()
    {
        var packets = new List<(byte[] Payload, ushort Sequence)>();
        var packetCountsAtWait = new List<int>();
        using var session = new P25TxCallSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, _) => packets.Add((payload.ToArray(), sequence)),
            waitForNextPacket: _ =>
            {
                packetCountsAtWait.Add(packets.Count);
                return ValueTask.CompletedTask;
            });
        session.Start();
        session.Process(new short[160]);

        await session.EndAsync();

        Assert.Equal([0, 1, 2], packetCountsAtWait);
        Assert.Equal(P25DfsiFrameCodec.Ldu1Duid, packets[1].Payload[22]);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[2].Payload[22]);
    }

    [Fact]
    public async Task FailedCompletionRetriesTheTerminatorWithoutRebuildingTheCallTail()
    {
        bool failNextPacket = false;
        int failedPackets = 0;
        var packets = new List<(byte[] Payload, ushort Sequence)>();
        using var session = new P25TxCallSession(
            sourceId: 1,
            destinationId: 2,
            streamId: 3,
            vocoder: new FakeVocoderSession(),
            send: (payload, sequence, _) =>
            {
                if (failNextPacket)
                {
                    failNextPacket = false;
                    failedPackets++;
                    throw new IOException("transient transport failure");
                }
                packets.Add((payload.ToArray(), sequence));
            },
            waitForNextPacket: TestPacketCadence.NoDelayAsync);
        session.Start();
        failNextPacket = true;

        await Assert.ThrowsAsync<IOException>(() => session.EndAsync().AsTask());
        await session.EndAsync();

        Assert.Equal(1, failedPackets);
        Assert.True(session.IsEnded);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, packets[^1].Payload[22]);
        Assert.Equal(P25DfsiFrameCodec.RtpCallEndSequence, packets[^1].Sequence);
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
}
