using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using fnecore;
using Xunit;

namespace DvmConsole.Fne.Tests;

public sealed class FneProtocolTests
{
    [Fact]
    public async Task EncryptedUdpFrameUsesServerCompatibleEcbEnvelope()
    {
        const string keyHex =
            "000102030405060708090A0B0C0D0E0F000102030405060708090A0B0C0D0E0F";
        byte[] plaintext = Convert.FromHexString(
            "905600001376E9000000037900FE0004" +
            "C9F060FFE3CBE0C60000037900000008" +
            "5250544C000003790000000000000000");

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);
        var sender = new UdpReceiver();
        sender.SetPresharedKey(FneUtils.ConvertHexStringToPresharedKey(keyHex));
        sender.Connect(destination);

        sender.Send(new UdpFrame
        {
            Endpoint = destination,
            Message = plaintext
        });

        UdpReceiveResult result = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        byte[] wire = result.Buffer;

        Assert.Equal(new byte[] { 0xC0, 0xFE }, wire[..2]);
        Assert.Equal(0, (wire.Length - 2) % 16);

        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = Convert.FromHexString(keyHex);
        aes.BlockSize = 128;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        byte[] decrypted = decryptor.TransformFinalBlock(wire, 2, wire.Length - 2);

        Assert.Equal(plaintext, decrypted[..plaintext.Length]);
        Assert.All(decrypted[plaintext.Length..], value => Assert.Equal((byte)0, value));
    }

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
