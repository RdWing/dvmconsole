using fnecore;
using Xunit;

namespace DvmConsole.Fne.Tests;

public sealed class FneProtocolTests
{
    [Fact]
    public void RtpHeaderRoundTrips()
    {
        RtpHeader.ResetStartTime();
        var source = new RtpHeader
        {
            Extension = true,
            Marker = true,
            PayloadType = 0x62,
            Sequence = 0x1234,
            SSRC = 0x10203040
        };
        byte[] encoded = new byte[12];

        source.Encode(ref encoded);

        var decoded = new RtpHeader();
        Assert.True(decoded.Decode(encoded));
        Assert.True(decoded.Extension);
        Assert.True(decoded.Marker);
        Assert.Equal(source.PayloadType, decoded.PayloadType);
        Assert.Equal(source.Sequence, decoded.Sequence);
        Assert.Equal(source.Timestamp, decoded.Timestamp);
        Assert.Equal(source.SSRC, decoded.SSRC);
    }

    [Fact]
    public void RtpFneHeaderRoundTrips()
    {
        var source = new RtpFNEHeader
        {
            CRC = 0x1234,
            Function = 0x56,
            SubFunction = 0x78,
            StreamID = 0x10203040,
            PeerID = 0x50607080,
            MessageLength = 0x90A0B0C0
        };
        byte[] encoded = new byte[32];

        source.Encode(ref encoded);

        var decoded = new RtpFNEHeader();
        Assert.True(decoded.Decode(encoded));
        Assert.Equal(source.CRC, decoded.CRC);
        Assert.Equal(source.Function, decoded.Function);
        Assert.Equal(source.SubFunction, decoded.SubFunction);
        Assert.Equal(source.StreamID, decoded.StreamID);
        Assert.Equal(source.PeerID, decoded.PeerID);
        Assert.Equal(source.MessageLength, decoded.MessageLength);
    }

    [Fact]
    public void PacketBufferReassemblesOutOfOrderFragments()
    {
        byte[] payload = Enumerable.Range(0, PacketBuffer.FRAG_BLOCK_SIZE * 2 + 37)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var encoder = new PacketBuffer(compression: false, name: "test-encoder");

        encoder.Encode(payload);

        Assert.Equal(3, encoder.Fragments.Count);

        var decoder = new PacketBuffer(compression: false, name: "test-decoder");
        bool completed = false;
        foreach (PacketBuffer.Fragment fragment in encoder.Fragments.Values.OrderByDescending(item => item.BlockId))
        {
            if (decoder.Decode(fragment.Data, out byte[] message, out uint length))
            {
                completed = true;
                Assert.Equal((uint)payload.Length, length);
                Assert.Equal(payload, message);
            }
        }

        Assert.True(completed);
    }

    [Fact]
    public void CreatesFneOpcode()
    {
        Tuple<byte, byte> opcode = FneBase.CreateOpcode(0x12, 0x34);

        Assert.Equal((byte)0x12, opcode.Item1);
        Assert.Equal((byte)0x34, opcode.Item2);
    }
}
