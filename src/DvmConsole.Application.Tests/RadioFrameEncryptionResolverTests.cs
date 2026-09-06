// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RadioFrameEncryptionResolverTests
{
    [Fact]
    public void OrdinaryDmrVoiceContinuationsDoNotReadPayloadOrAllocateCacheEntries()
    {
        var frames = Enumerable.Range(0, 10_000)
            .Select(index => new CountingFrame(
                [0x44, 0x4D, 0x52, 0x44],
                RadioMediaProtocol.Dmr,
                frameType: "VOICE",
                subtype: "VOICE",
                packetSequence: (ushort)index))
            .ToArray();

        _ = RadioFrameEncryptionResolver.TryResolve(frames[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (CountingFrame frame in frames)
            Assert.Null(RadioFrameEncryptionResolver.TryResolve(frame));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.All(frames, frame => Assert.Equal(0, frame.PayloadReads));
    }

    [Fact]
    public void ReusesParsedEncryptionMetadataForEveryDownstreamConsumer()
    {
        byte[] payload = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId: 42,
            destinationId: 99,
            group: true,
            messageType: NxdnVoicePacketCodec.VoiceCallMessageType,
            frameSequence: 0,
            cipherType: 2,
            keyId: 7);
        var frame = new CountingFrame(payload, RadioMediaProtocol.Nxdn);

        RadioFrameEncryption? first = RadioFrameEncryptionResolver.TryResolve(frame);
        RadioFrameEncryption? second = RadioFrameEncryptionResolver.TryResolve(frame);
        bool hasMetadata = RadioFrameEncryptionResolver.TryResolveNxdnCallMetadata(
            frame,
            out NxdnVoicePacketCodec.CallMetadata metadata);

        Assert.Equal(new RadioFrameEncryption(true, 2, 7), first);
        Assert.Equal(first, second);
        Assert.True(hasMetadata);
        Assert.Equal(NxdnVoicePacketCodec.VoiceCallMessageType, metadata.MessageType);
        Assert.Equal(1, frame.PayloadReads);
    }

    private sealed class CountingFrame(
        byte[] payload,
        RadioMediaProtocol protocol,
        string frameType = "VOICE",
        string subtype = "VOICE",
        ushort packetSequence = 1) : IRadioMediaFrame
    {
        public int PayloadReads { get; private set; }
        public RadioMediaProtocol Protocol => protocol;
        public uint PeerId => 1;
        public uint SourceId => 42;
        public uint DestinationId => 99;
        public byte? Slot => null;
        public string CallType => "GROUP";
        public string FrameType => frameType;
        public string Subtype => subtype;
        public ushort PacketSequence => packetSequence;
        public uint StreamId => 10;
        public byte[] Payload
        {
            get
            {
                PayloadReads++;
                return payload;
            }
        }
    }
}
