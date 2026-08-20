using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using fnecore;
using fnecore.EDAC;
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

        using IDisposable encryptionScope = FneTransportEncryptionContext.Use(FneTransportEncryptionMode.Ecb);
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
        byte[] decrypted = DecryptEcb(wire, keyHex);

        Assert.Equal(plaintext, decrypted[..plaintext.Length]);
        Assert.All(decrypted[plaintext.Length..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task EncryptedUdpFrameSupportsCbcEnvelopeWithTrailingIv()
    {
        const string keyHex =
            "000102030405060708090A0B0C0D0E0F000102030405060708090A0B0C0D0E0F";
        byte[] plaintext = CreateLoginFrame();

        using IDisposable encryptionScope = FneTransportEncryptionContext.Use(FneTransportEncryptionMode.Cbc);
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = Assert.IsType<IPEndPoint>(listener.Client.LocalEndPoint);
        var sender = new UdpReceiver();
        sender.SetPresharedKey(Convert.FromHexString(keyHex));
        sender.Connect(destination);

        sender.Send(new UdpFrame { Endpoint = destination, Message = plaintext });
        UdpReceiveResult result = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new byte[] { 0xC0, 0xFE }, result.Buffer[..2]);
        Assert.Equal(0, (result.Buffer.Length - 18) % 16);
        Assert.Equal(plaintext, DecryptCbc(result.Buffer, keyHex));
    }

    [Fact]
    public async Task AutoEncryptionAlternatesThenLocksToValidatedServerMode()
    {
        const string keyHex =
            "000102030405060708090A0B0C0D0E0F000102030405060708090A0B0C0D0E0F";
        byte[] plaintext = CreateLoginFrame();

        using IDisposable encryptionScope = FneTransportEncryptionContext.Use(FneTransportEncryptionMode.Auto);
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = Assert.IsType<IPEndPoint>(server.Client.LocalEndPoint);
        var client = new UdpReceiver();
        client.SetPresharedKey(Convert.FromHexString(keyHex));
        client.Connect(destination);

        client.Send(new UdpFrame { Endpoint = destination, Message = plaintext });
        UdpReceiveResult first = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(plaintext, DecryptEcb(first.Buffer, keyHex));

        client.Send(new UdpFrame { Endpoint = destination, Message = plaintext });
        UdpReceiveResult second = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(plaintext, DecryptCbc(second.Buffer, keyHex));

        byte[] cbcResponse = EncryptCbc(plaintext, keyHex);
        await server.SendAsync(cbcResponse, second.RemoteEndPoint);
        UdpFrame response = await client.Receive().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(plaintext, response.Message);
        Assert.Equal(FneTransportEncryptionMode.Cbc, client.NegotiatedEncryptionMode);

        client.Send(new UdpFrame { Endpoint = destination, Message = plaintext });
        UdpReceiveResult third = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(plaintext, DecryptCbc(third.Buffer, keyHex));
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

    [Fact]
    public void RejectsFramesThatCanReachUnsafeUpstreamParsers()
    {
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(new byte[31]));

        byte[] validAck = CreateFrame(Constants.NET_FUNC_ACK, Constants.NET_SUBFUNC_NOP, new byte[10]);
        Assert.True(FneInboundFramePolicy.ShouldDeliverTraffic(validAck));

        byte[] oversized = validAck.ToArray();
        FneUtils.WriteBytes(uint.MaxValue, ref oversized, 28);
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(oversized));

        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_PROTOCOL,
            Constants.NET_PROTOCOL_SUBFUNC_P25,
            new byte[22])));
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_INCALL_CTRL,
            Constants.NET_PROTOCOL_SUBFUNC_DMR,
            new byte[14])));

        byte[] oversizedHaTable = new byte[10];
        FneUtils.WriteBytes(uint.MaxValue, ref oversizedHaTable, 6);
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_MASTER,
            Constants.NET_MASTER_SUBFUNC_HA_PARAMS,
            oversizedHaTable)));

        byte[] malformedKmm = new byte[29];
        malformedKmm[11] = (byte)fnecore.P25.KMM.KmmMessageType.MODIFY_KEY_CMD;
        malformedKmm[25] = 0;
        malformedKmm[26] = fnecore.P25.P25Defines.P25_ALGO_AES;
        malformedKmm[27] = 32;
        malformedKmm[28] = 1;
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_KEY_RSP,
            Constants.NET_SUBFUNC_NOP,
            malformedKmm)));
    }

    [Fact]
    public void DisablesUnusedMetadataAndAnnouncementInputs()
    {
        Assert.False(FneInboundFramePolicy.AcceptsInbound(FneUdpChannelKind.Metadata));
        Assert.True(FneInboundFramePolicy.AcceptsInbound(FneUdpChannelKind.Traffic));

        using (FneTransportEncryptionContext.Use(FneTransportEncryptionMode.Auto))
        {
            Assert.Equal(FneUdpChannelKind.Traffic, FneTransportEncryptionContext.Capture().ChannelKind);
            Assert.Equal(FneUdpChannelKind.Metadata, FneTransportEncryptionContext.Capture().ChannelKind);
        }

        byte[] announcement = new byte[11];
        FneUtils.WriteBytes(0U, ref announcement, 6);
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_MASTER,
            Constants.NET_MASTER_SUBFUNC_ACTIVE_TGS,
            announcement)));
        Assert.False(FneInboundFramePolicy.ShouldDeliverTraffic(CreateFrame(
            Constants.NET_FUNC_MASTER,
            Constants.NET_MASTER_SUBFUNC_DEACTIVE_TGS,
            announcement)));
    }

    [Fact]
    public async Task EncryptedReceiverDropsExactWireReplayAndContinues()
    {
        const string keyHex =
            "000102030405060708090A0B0C0D0E0F000102030405060708090A0B0C0D0E0F";
        byte[] firstFrame = CreateFrame(Constants.NET_FUNC_ACK, Constants.NET_SUBFUNC_NOP, new byte[10]);
        byte[] secondPayload = new byte[10];
        secondPayload[^1] = 1;
        byte[] secondFrame = CreateFrame(Constants.NET_FUNC_ACK, Constants.NET_SUBFUNC_NOP, secondPayload);

        using IDisposable encryptionScope = FneTransportEncryptionContext.Use(FneTransportEncryptionMode.Cbc);
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var destination = Assert.IsType<IPEndPoint>(server.Client.LocalEndPoint);
        var receiver = new UdpReceiver();
        receiver.SetPresharedKey(Convert.FromHexString(keyHex));
        receiver.Connect(destination);
        receiver.Send(new UdpFrame { Endpoint = destination, Message = firstFrame });
        UdpReceiveResult probe = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        IPEndPoint receiverEndpoint = probe.RemoteEndPoint;

        byte[] firstWire = EncryptCbc(PadToBlock(firstFrame), keyHex);
        await server.SendAsync(firstWire, receiverEndpoint);
        UdpFrame first = await receiver.Receive().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(firstFrame, first.Message[..firstFrame.Length]);

        Task<UdpFrame> nextReceive = receiver.Receive();
        await server.SendAsync(firstWire, receiverEndpoint);
        await server.SendAsync(EncryptCbc(PadToBlock(secondFrame), keyHex), receiverEndpoint);
        UdpFrame second = await nextReceive.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(secondFrame, second.Message[..secondFrame.Length]);
    }

    private static byte[] CreateLoginFrame()
        => Convert.FromHexString(
            "905600001376E9000000037900FE0004" +
            "C9F060FFE3CBE0C60000037900000008" +
            "5250544C000003790000000000000000");

    private static byte[] CreateFrame(byte function, byte subFunction, byte[] payload)
    {
        byte[] frame = new byte[32 + payload.Length];
        var rtp = new RtpHeader
        {
            Extension = true,
            PayloadType = Constants.DVMRtpPayloadType,
            Sequence = 1,
            SSRC = 1
        };
        rtp.Encode(ref frame);

        var fne = new RtpFNEHeader
        {
            CRC = CRC.CreateCRC16(payload, (uint)(payload.Length * 8)),
            Function = function,
            SubFunction = subFunction,
            StreamID = 1,
            PeerID = 1,
            MessageLength = (uint)payload.Length
        };
        fne.Encode(ref frame);
        payload.CopyTo(frame, 32);
        return frame;
    }

    private static byte[] PadToBlock(byte[] plaintext)
    {
        int paddedLength = ((plaintext.Length + 15) / 16) * 16;
        return paddedLength == plaintext.Length
            ? plaintext
            : plaintext.Concat(new byte[paddedLength - plaintext.Length]).ToArray();
    }

    private static byte[] DecryptEcb(byte[] wire, string keyHex)
        => Transform(wire[2..], Convert.FromHexString(keyHex), CipherMode.ECB, encrypt: false, null);

    private static byte[] DecryptCbc(byte[] wire, string keyHex)
    {
        byte[] decrypted = Transform(
            wire[2..^16],
            Convert.FromHexString(keyHex),
            CipherMode.CBC,
            encrypt: false,
            wire[^16..]);
        return decrypted;
    }

    private static byte[] EncryptCbc(byte[] plaintext, string keyHex)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] encrypted = Transform(
            plaintext,
            Convert.FromHexString(keyHex),
            CipherMode.CBC,
            encrypt: true,
            iv);
        byte[] wire = new byte[2 + encrypted.Length + iv.Length];
        wire[0] = 0xC0;
        wire[1] = 0xFE;
        encrypted.CopyTo(wire, 2);
        iv.CopyTo(wire, 2 + encrypted.Length);
        return wire;
    }

    private static byte[] Transform(
        byte[] input,
        byte[] key,
        CipherMode mode,
        bool encrypt,
        byte[]? iv)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.Mode = mode;
        aes.Padding = PaddingMode.None;
        if (iv is not null)
            aes.IV = iv;
        using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(input, 0, input.Length);
    }
}
