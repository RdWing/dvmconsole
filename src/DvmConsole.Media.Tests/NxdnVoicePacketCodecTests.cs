// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class NxdnVoicePacketCodecTests
{
    [Fact]
    public void VoicePacketRoundTripsFourAmbeCodewords()
    {
        byte[] expected = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)(0x40 + value))
            .ToArray();

        byte[] packet = NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 7, expected);
        byte[] actual = new byte[NxdnVoicePacketCodec.AmbeBytes];

        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packet, actual, out int count));
        Assert.Equal(4, count);
        Assert.Equal(expected, actual);
        Assert.Equal("NXDD", System.Text.Encoding.ASCII.GetString(packet, 0, 4));
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, packet[4]);
        Assert.Equal(NxdnVoicePacketCodec.PacketBytes, packet.Length);
        Assert.Equal(NxdnVoicePacketCodec.DeclaredPacketBytes, packet[23]);
        Assert.Equal(0x01, packet[24]);
        Assert.Equal(0x00, packet[25]);
    }

    [Fact]
    public void CallerOwnedVoiceWriterMatchesAllocatedPacketWithoutAllocating()
    {
        byte[] ambe = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)(0x40 + value))
            .ToArray();
        byte[] expected = NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 7, ambe);
        byte[] packet = new byte[NxdnVoicePacketCodec.PacketBytes];

        NxdnVoicePacketCodec.WriteVoicePacket(packet, 1001, 2002, true, 7, ambe);
        Assert.Equal(expected, packet);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
            NxdnVoicePacketCodec.WriteVoicePacket(packet, 1001, 2002, true, 7, ambe);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SinglePassVoiceParseMatchesCompatibilityExtractors()
    {
        byte[] expected = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)(0x40 + value))
            .ToArray();
        byte[] packet = NxdnVoicePacketCodec.CreateVoicePacket(
            1001,
            2002,
            true,
            7,
            expected,
            superframePart: 2);
        byte[] compatibilityAmbe = new byte[NxdnVoicePacketCodec.AmbeBytes];
        byte[] compatibilitySacch = new byte[3];
        byte[] singlePassAmbe = new byte[NxdnVoicePacketCodec.AmbeBytes];
        byte[] singlePassSacch = new byte[3];

        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(packet, compatibilityAmbe, out int compatibilityCount));
        Assert.True(NxdnVoicePacketCodec.TryExtractSacchFragment(packet, out byte compatibilityStructure, compatibilitySacch));
        Assert.True(NxdnVoicePacketCodec.TryParseVoicePacket(
            packet,
            singlePassAmbe,
            singlePassSacch,
            out NxdnVoicePacketCodec.ParsedVoicePacket parsed));

        Assert.Equal(compatibilityCount, parsed.AmbeCodewordCount);
        Assert.Equal(compatibilityStructure, parsed.SacchStructure);
        Assert.True(parsed.HasSacchFragment);
        Assert.Equal(compatibilityAmbe, singlePassAmbe);
        Assert.Equal(compatibilitySacch, singlePassSacch);
    }

    [Fact]
    public void SinglePassControlParsePreservesBothFacchMessages()
    {
        byte[] messageIndicator = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] packet = NxdnVoicePacketCodec.CreatePrivacyCallStartPacket(
            1001,
            2002,
            true,
            7,
            NxdnPrivacyAlgorithms.Aes256,
            5,
            messageIndicator);

        Assert.True(NxdnVoicePacketCodec.TryParseVoicePacket(
            packet,
            new byte[NxdnVoicePacketCodec.AmbeBytes],
            new byte[3],
            out NxdnVoicePacketCodec.ParsedVoicePacket parsed));

        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, parsed.FirstFacchMetadata?.MessageType);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallIvMessageType, parsed.SecondFacchMetadata?.MessageType);
        Assert.Equal(messageIndicator, parsed.SecondFacchMetadata?.MessageIndicator);
        Assert.Equal(0, parsed.AmbeCodewordCount);
    }

    [Fact]
    public void LegacyHeaderVoicePacketStillExtractsFourAmbeCodewords()
    {
        byte[] expected = Enumerable.Range(0, NxdnVoicePacketCodec.AmbeBytes)
            .Select(value => (byte)(0x20 + value))
            .ToArray();
        byte[] current = NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 7, expected);
        byte[] frame = NxdnVoicePacketCodec.ExtractFrame(current);
        byte[] legacy = new byte[NxdnVoicePacketCodec.LegacyPacketBytes];
        current.AsSpan(0, NxdnVoicePacketCodec.LegacyHeaderBytes).CopyTo(legacy);
        frame.CopyTo(legacy, NxdnVoicePacketCodec.LegacyHeaderBytes);

        byte[] actual = new byte[NxdnVoicePacketCodec.AmbeBytes];
        Assert.True(NxdnVoicePacketCodec.TryExtractAmbe(legacy, actual, out int count));
        Assert.Equal(4, count);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(NxdnVoicePacketCodec.VoiceCallMessageType)]
    [InlineData(NxdnVoicePacketCodec.TransmitReleaseMessageType)]
    public void CallControlRoundTripsFacchMetadata(byte messageType)
    {
        byte[] packet = NxdnVoicePacketCodec.CreateCallControlPacket(
            1001, 2002, false, messageType, 3, cipherType: 2, keyId: 5);

        Assert.True(NxdnVoicePacketCodec.TryExtractCallMetadata(packet, out var metadata));
        Assert.Equal(messageType, metadata.MessageType);
        Assert.Equal((ushort)1001, metadata.SourceId);
        Assert.Equal((ushort)2002, metadata.DestinationId);
        Assert.False(metadata.Group);
        Assert.Equal(messageType == NxdnVoicePacketCodec.VoiceCallMessageType ? (byte)2 : (byte)0, metadata.CipherType);
        Assert.Equal(messageType == NxdnVoicePacketCodec.VoiceCallMessageType ? (byte)5 : (byte)0, metadata.KeyId);
    }

    [Fact]
    public async Task CallSessionSendsHeaderVoiceAndReleaseInOrder()
    {
        var sent = new List<(byte[] Payload, ushort Sequence)>();
        using var call = new NxdnTxCallSession(
            1001,
            2002,
            group: true,
            streamId: 99,
            new FakeVocoderSession(),
            (payload, sequence, _) => sent.Add((payload.ToArray(), sequence)),
            TestPacketCadence.NoDelayAsync);

        call.Start();
        call.Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * NxdnVoicePacketCodec.CodewordsPerFrame]);
        await call.EndAsync();

        Assert.Equal(4, sent.Count);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[0].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[1].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, sent[2].Payload[4]);
        Assert.Equal(NxdnVoicePacketCodec.TransmitReleaseMessageType, sent[3].Payload[4]);
        Assert.Equal([0, 1, 2, 3], sent.Select(item => (int)item.Sequence));
    }

    [Fact]
    public async Task ReceiveSessionConcealsFourVoiceSlotsForOneMissingPacket()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002),
            vocoder,
            playback);
        byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];

        Assert.Equal(0, await session.ProcessAsync(Traffic(
            NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 1, ambe),
            sequence: 1)));
        Assert.Equal(0, await session.ProcessAsync(Traffic(
            NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 3, ambe),
            sequence: 3)));

        Assert.Equal(1, session.LostPackets);
        Assert.Equal(12, session.FramesDecoded);
        Assert.Equal(8, vocoder.DecodeCalls);
        Assert.Equal(4, vocoder.DecodeLostCalls);
        Assert.Equal(12, playback.Frames.Count);
    }

    [Fact]
    public async Task ReceiveSessionConcealsMalformedPacketAfterVoiceHasStarted()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002),
            vocoder,
            playback);
        byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];

        Assert.Equal(0, await session.ProcessAsync(Traffic(
            NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 1, ambe),
            sequence: 1)));
        Assert.Equal(0, await session.ProcessAsync(Traffic(new byte[10], sequence: 2)));

        Assert.Equal(1, session.MalformedPackets);
        Assert.Equal(8, session.FramesDecoded);
        Assert.Equal(4, vocoder.DecodeCalls);
        Assert.Equal(4, vocoder.DecodeLostCalls);
        Assert.Equal(8, playback.Frames.Count);
    }

    [Fact]
    public async Task ReceiveSessionBoundsLongLossAndResetsTheDecoder()
    {
        var vocoder = new FakeVocoderSession();
        var playback = new FakePlayback();
        await using var session = new NxdnRxAudioSession(
            new NxdnTrafficSelector(2002),
            vocoder,
            playback);
        byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];

        Assert.Equal(0, await session.ProcessAsync(Traffic(
            NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 1, ambe),
            sequence: 1)));
        Assert.Equal(0, await session.ProcessAsync(Traffic(
            NxdnVoicePacketCodec.CreateVoicePacket(1001, 2002, true, 13, ambe),
            sequence: 13)));

        Assert.Equal(11, session.LostPackets);
        Assert.Equal(48, session.FramesDecoded);
        Assert.Equal(8, vocoder.DecodeCalls);
        Assert.Equal(40, vocoder.DecodeLostCalls);
        Assert.Equal(1, vocoder.ResetCalls);
        Assert.Equal(48, playback.Frames.Count);
    }

    private static FneTrafficFrame Traffic(byte[] payload, ushort sequence)
        => new(
            FneTrafficProtocol.Nxdn,
            peerId: 1,
            sourceId: 1001,
            destinationId: 2002,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "VCALL",
            sequence,
            streamId: 99,
            payload);

    private sealed class FakeVocoderSession : IVocoderSession
    {
        private byte value;
        public int DecodeCalls { get; private set; }
        public int DecodeLostCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Fill(++value);
            return 0;
        }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            DecodeCalls++;
            samples.Fill((short)DecodeCalls);
            return 0;
        }
        public int DecodeLost(Span<short> samples)
        {
            DecodeLostCalls++;
            samples.Clear();
            return 0;
        }
        public int FlushEncode(Span<byte> codeword)
        {
            codeword.Fill(++value);
            return codeword.Length;
        }
        public void Reset() => ResetCalls++;
        public void Dispose() { }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public List<short[]> Frames { get; } = [];
        public PcmAudioFormat Format { get; } = PcmAudioFormat.Voice8KhzMono16Bit;

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(samples.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
