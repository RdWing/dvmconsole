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

    private static FneTrafficFrame Traffic(ushort sequence, bool terminator = false)
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
            99,
            []);
}
