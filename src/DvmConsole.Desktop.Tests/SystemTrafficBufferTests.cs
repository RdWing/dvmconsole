using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SystemTrafficBufferTests
{
    [Fact]
    public void BoundsVoiceBacklogAndRetainsTerminator()
    {
        var buffer = new SystemTrafficBuffer(maximumCount: 2);

        buffer.Enqueue(Traffic(1));
        buffer.Enqueue(Traffic(2));
        buffer.Enqueue(Traffic(3));
        buffer.Enqueue(Traffic(4, terminator: true));

        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.DroppedCount);
        Assert.True(buffer.TryDequeue(out FneTrafficFrame? first));
        Assert.True(buffer.TryDequeue(out FneTrafficFrame? second));
        Assert.Contains(new[] { first!, second! }, traffic => traffic.PacketSequence == 4);
    }

    [Fact]
    public void RetainsP25EncryptionMetadataAheadOfStaleVoice()
    {
        var buffer = new SystemTrafficBuffer(maximumCount: 2);
        var metadata = new FneTrafficFrame(
            FneTrafficProtocol.P25, 1, 2, 100, null, "GROUP", "VOICE", "LDU1", 1, 99,
            P25DfsiFrameCodec.CreateLdu1Payload(2, 100, new byte[P25DfsiFrameCodec.ImbeBytes]));

        buffer.Enqueue(metadata);
        buffer.Enqueue(Traffic(2));
        buffer.Enqueue(Traffic(3));

        Assert.Equal(1, buffer.DroppedCount);
        Assert.True(buffer.TryDequeue(out FneTrafficFrame? retained));
        Assert.Same(metadata, retained);
    }

    [Fact]
    public void RetainsNxdnVoiceCallIvAheadOfStaleVoice()
    {
        var buffer = new SystemTrafficBuffer(maximumCount: 2);
        var callIv = new FneTrafficFrame(
            FneTrafficProtocol.Nxdn, 1, 2, 100, null, "GROUP", "VOICE", "VCALL_IV", 1, 99,
            NxdnVoicePacketCodec.CreateCallControlPacket(
                2, 100, true, NxdnVoicePacketCodec.VoiceCallIvMessageType, 0,
                messageIndicator: new byte[8]));

        buffer.Enqueue(callIv);
        buffer.Enqueue(Traffic(2));
        buffer.Enqueue(Traffic(3));

        Assert.True(buffer.TryDequeue(out FneTrafficFrame? retained));
        Assert.Same(callIv, retained);
    }

    [Fact]
    public void RetainsDmrVoiceLinkControlHeaderAheadOfStaleVoice()
    {
        var buffer = new SystemTrafficBuffer(maximumCount: 2);
        var header = new FneTrafficFrame(
            FneTrafficProtocol.Dmr, 1, 2, 100, 0, "GROUP", "DATA_SYNC", "VOICE_LC_HEADER", 1, 99, []);

        buffer.Enqueue(header);
        buffer.Enqueue(Traffic(2));
        buffer.Enqueue(Traffic(3));

        Assert.True(buffer.TryDequeue(out FneTrafficFrame? retained));
        Assert.Same(header, retained);
    }

    [Fact]
    public void TerminatorRetainsAQueuedVoiceFrameForItsShortStream()
    {
        var buffer = new SystemTrafficBuffer(maximumCount: 2);

        buffer.Enqueue(Traffic(1, streamId: 100));
        buffer.Enqueue(Traffic(2, streamId: 200));
        buffer.Enqueue(Traffic(3, terminator: true, streamId: 100));

        Assert.True(buffer.TryDequeue(out FneTrafficFrame? first));
        Assert.True(buffer.TryDequeue(out FneTrafficFrame? second));
        Assert.Equal((uint)100, first!.StreamId);
        Assert.Equal((uint)100, second!.StreamId);
        Assert.Equal("VOICE", first.FrameType);
        Assert.Equal("TERMINATOR", second.FrameType);
    }

    private static FneTrafficFrame Traffic(
        ushort sequence,
        bool terminator = false,
        uint streamId = 99)
        => new(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            0,
            "GROUP",
            terminator ? "TERMINATOR" : "VOICE",
            terminator ? "TERMINATOR_WITH_LC" : "VOICE",
            sequence,
            streamId,
            []);
}
