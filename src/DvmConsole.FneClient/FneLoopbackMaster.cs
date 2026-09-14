// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using fnecore;
using fnecore.P25.KMM;

namespace DvmConsole.FneClient;

/// <summary>Owned loopback-only FNE fixture for explicit native qualification.</summary>
public sealed class FneLoopbackMaster : IAsyncDisposable
{
    private const string Password = "local-qualification-only";
    private const uint Salt = 0x12345678;
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource lifetime = new();
    private readonly FrameCodec frames = new();
    private readonly Channel<byte[]> subscriberFrames = Channel.CreateBounded<byte[]>(16);
    private readonly Channel<(byte Algorithm, ushort Key)> keyRequests = Channel.CreateBounded<(byte, ushort)>(16);
    private readonly Task serving;
    private Peer? peer;
    private int authentications;
    private int disposed;

    public FneLoopbackMaster() => serving = ServeAndCompleteAsync();
    public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    public int Authentications => Volatile.Read(ref authentications);
    public Task Completion => serving;
    public ValueTask<byte[]> ReadSubscriberCommandAsync(CancellationToken cancellationToken = default)
        => subscriberFrames.Reader.ReadAsync(cancellationToken);
    public FneConnectionOptions CreateOptions(string name = "Loopback")
        => new(name, "Console qualification", "127.0.0.1", Port, 1001, Password, false, null);

    public async ValueTask SendTrafficAsync(FneTrafficProtocol protocol, byte[] payload,
        ushort sequence, uint stream, CancellationToken cancellationToken = default)
    {
        Peer current = Volatile.Read(ref peer) ?? throw new InvalidOperationException("No configured loopback peer.");
        byte subfunction = protocol switch
        {
            FneTrafficProtocol.Dmr => Constants.NET_PROTOCOL_SUBFUNC_DMR,
            FneTrafficProtocol.P25 => Constants.NET_PROTOCOL_SUBFUNC_P25,
            FneTrafficProtocol.Nxdn => Constants.NET_PROTOCOL_SUBFUNC_NXDN,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        byte[] packet = frames.Traffic(payload, current.Id, stream, sequence, subfunction);
        await socket.SendAsync(packet, current.Endpoint, cancellationToken);
    }

    public ValueTask SendSubscriberAcknowledgementAsync(P25SubscriberCommand command, uint subscriberId,
        uint destinationId, CancellationToken cancellationToken = default)
    {
        if (!P25SubscriberCommandCodec.IsValidSubscriberId(subscriberId) ||
            !P25SubscriberCommandCodec.IsValidSubscriberId(destinationId))
            throw new ArgumentOutOfRangeException(nameof(subscriberId));
        byte[] block = new byte[12];
        block[0] = command == P25SubscriberCommand.CallAlert ? (byte)0xA0 : (byte)0xA4;
        if (command == P25SubscriberCommand.CallAlert)
        {
            block[2] = 0x9F;
            FneUtils.Write3Bytes(destinationId, ref block, 4);
            FneUtils.Write3Bytes(subscriberId, ref block, 7);
        }
        else
        {
            ushort function = command switch
            {
                P25SubscriberCommand.RadioCheck => 0x0080,
                P25SubscriberCommand.Inhibit => 0x00FF,
                P25SubscriberCommand.Uninhibit => 0x00FE,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(2), function);
            FneUtils.Write3Bytes(subscriberId, ref block, 4);
            FneUtils.Write3Bytes(destinationId, ref block, 7);
        }
        fnecore.EDAC.CRC.AddCCITT162(ref block, 12);
        byte opcode = (byte)(block[0] & 0x3F);
        byte[] payload = P25SubscriberFrameEncoder.Encode(new(command, subscriberId, destinationId, opcode, block),
            new RemoteCallData { SrcId = subscriberId, DstId = destinationId, LCO = opcode });
        return SendTrafficAsync(FneTrafficProtocol.P25, payload, 0, 42, cancellationToken);
    }

    public ValueTask<(byte Algorithm, ushort Key)> ReadKeyRequestAsync(CancellationToken cancellationToken = default)
        => keyRequests.Reader.ReadAsync(cancellationToken);

    public async ValueTask SendKeyResponseAsync(byte algorithm, ushort key, ReadOnlyMemory<byte> material,
        CancellationToken cancellationToken = default)
    {
        Peer current = Volatile.Read(ref peer) ?? throw new InvalidOperationException("No configured loopback peer.");
        if (material.Length is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(material));
        var item = new KeyItem { KeyId = key };
        item.SetKey(material.ToArray(), (uint)material.Length);
        var response = new KmmModifyKey { AlgId = algorithm, KeyId = key };
        response.KeysetItem.AlgId = algorithm;
        response.KeysetItem.KeyLength = (byte)material.Length;
        response.KeysetItem.AddKey(item);
        response.MessageLength = (ushort)(14 + response.KeysetItem.Length);
        byte[] encoded = new byte[response.MessageLength];
        response.Encode(encoded);
        byte[] payload = new byte[11 + encoded.Length];
        encoded.CopyTo(payload, 11);
        await socket.SendAsync(frames.EncodeKeyResponse(payload, current.Id), current.Endpoint, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        socket.Dispose();
        try { await serving; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    private sealed record Peer(IPEndPoint Endpoint, uint Id);

    private async Task ServeAndCompleteAsync()
    {
        try { await ServeAsync(); }
        catch (Exception exception)
        {
            subscriberFrames.Writer.TryComplete(lifetime.IsCancellationRequested ? null : exception);
            keyRequests.Writer.TryComplete(lifetime.IsCancellationRequested ? null : exception);
            throw;
        }
        finally { subscriberFrames.Writer.TryComplete(); keyRequests.Writer.TryComplete(); }
    }

    private async Task ServeAsync()
    {
        ushort sequence = 0;
        while (true)
        {
            UdpReceiveResult incoming = await socket.ReceiveAsync(lifetime.Token);
            var header = new RtpFNEHeader();
            if (!header.Decode(incoming.Buffer)) throw new InvalidDataException("Invalid loopback FNE header.");
            int offset = checked((int)(Constants.RtpHeaderLengthBytes + Constants.RtpExtensionHeaderLengthBytes + Constants.RtpFNEHeaderLengthBytes));
            byte[] payload = incoming.Buffer[offset..];
            switch (header.Function)
            {
                case Constants.NET_FUNC_KEY_REQ:
                    if (payload.Length != 32 || payload[11] != (byte)KmmMessageType.MODIFY_KEY_CMD)
                        throw new InvalidDataException("Invalid loopback key request.");
                    if (!keyRequests.Writer.TryWrite((payload[22], BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(23)))))
                        throw new InvalidOperationException("Loopback key request queue overflowed.");
                    continue;
                case Constants.NET_FUNC_PROTOCOL:
                    if (header.SubFunction == Constants.NET_PROTOCOL_SUBFUNC_P25 && payload.Length == 200 && payload[22] == 0x07 &&
                        !subscriberFrames.Writer.TryWrite(payload))
                        throw new InvalidOperationException("Loopback subscriber command queue overflowed.");
                    continue;
                case Constants.NET_FUNC_PING:
                    byte[] pong = frames.Pong(header.PeerID, header.StreamID, sequence++);
                    await socket.SendAsync(pong, incoming.RemoteEndPoint, lifetime.Token);
                    continue;
                case Constants.NET_FUNC_RPT_CLOSING:
                    if (peer?.Endpoint.Equals(incoming.RemoteEndPoint) == true)
                        Volatile.Write(ref peer, null);
                    continue;
                case Constants.NET_FUNC_RPTL:
                    break;
                case Constants.NET_FUNC_RPTK:
                    byte[] material = new byte[4 + Password.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(material, Salt);
                    Encoding.ASCII.GetBytes(Password, material.AsSpan(4));
                    if (payload.Length != 40 || !CryptographicOperations.FixedTimeEquals(
                        payload.AsSpan(8), SHA256.HashData(material)))
                        throw new InvalidDataException("Loopback peer authentication did not match the challenge.");
                    Interlocked.Increment(ref authentications);
                    break;
                case Constants.NET_FUNC_RPTC:
                    if (payload.Length <= 8 || !Encoding.UTF8.GetString(payload, 8, payload.Length - 8)
                        .Contains("Console qualification", StringComparison.Ordinal))
                        throw new InvalidDataException("Loopback peer configuration was not received.");
                    break;
                default:
                    continue;
            }
            if (header.Function == Constants.NET_FUNC_RPTC)
                Volatile.Write(ref peer, new Peer(incoming.RemoteEndPoint, header.PeerID));
            byte[] acknowledgement = new byte[10];
            Encoding.ASCII.GetBytes("MSTACK", acknowledgement);
            BinaryPrimitives.WriteUInt32BigEndian(acknowledgement.AsSpan(6), Salt);
            byte[] response = frames.Acknowledge(acknowledgement, header.PeerID, header.StreamID, sequence++);
            await socket.SendAsync(response, incoming.RemoteEndPoint, lifetime.Token);
        }
    }
    // Reuse fnecore's production RTP extension and CRC encoding instead of
    // maintaining a second implementation of the FNE envelope in the fixture.
    private sealed class FrameCodec() : FneBase("Loopback master", 2001)
    {
        public byte[] Acknowledge(byte[] message, uint peer, uint stream, ushort sequence)
            => WriteFrame(message, peer, 2001, Tuple.Create(Constants.NET_FUNC_ACK, Constants.NET_SUBFUNC_NOP), sequence, stream);
        public byte[] Pong(uint peer, uint stream, ushort sequence)
            => WriteFrame(new byte[1], peer, 2001, Tuple.Create(Constants.NET_FUNC_PONG, Constants.NET_SUBFUNC_NOP), sequence, stream);
        public byte[] Traffic(byte[] message, uint peer, uint stream, ushort sequence, byte subfunction)
            => WriteFrame(message, peer, 2001, Tuple.Create(Constants.NET_FUNC_PROTOCOL, subfunction), sequence, stream);
        public byte[] EncodeKeyResponse(byte[] message, uint peer)
            => WriteFrame(message, peer, 2001, Tuple.Create(Constants.NET_FUNC_KEY_RSP, Constants.NET_SUBFUNC_NOP), Constants.RtpCallEndSeq, 1);
        public override void Start() => throw new NotSupportedException();
        public override void Stop() => throw new NotSupportedException();
    }
}
