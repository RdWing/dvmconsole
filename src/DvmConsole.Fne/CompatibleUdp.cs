#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Fixed Network Equipment Core Library
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Fixed Network Equipment Core Library
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2022,2024 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2026 RdWing
*
*/

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;

namespace fnecore;

public enum FneTransportEncryptionMode
{
    Auto,
    Ecb,
    Cbc
}

internal enum FneUdpChannelKind
{
    Traffic,
    Metadata
}

// FnePeer constructs its traffic receiver followed by its metadata receiver.
// This short-lived ambient scope lets each pinned peer capture an independent
// transport mode and channel role without maintaining a private fnecore fork.
public static class FneTransportEncryptionContext
{
    private static readonly AsyncLocal<ContextState?> Current = new();

    internal static (
        FneTransportEncryptionMode Mode,
        FneUdpChannelKind ChannelKind,
        Action<long>? TrafficIngressObserver) Capture()
    {
        ContextState? state = Current.Value;
        if (state is null)
            return (FneTransportEncryptionMode.Auto, FneUdpChannelKind.Traffic, null);

        FneUdpChannelKind channelKind = state.ReceiverCount++ == 1
            ? FneUdpChannelKind.Metadata
            : FneUdpChannelKind.Traffic;
        return (state.Mode, channelKind, state.TrafficIngressObserver);
    }

    public static IDisposable Use(FneTransportEncryptionMode mode)
        => Use(mode, trafficIngressObserver: null);

    public static IDisposable Use(
        FneTransportEncryptionMode mode,
        Action<long>? trafficIngressObserver)
    {
        ContextState? previous = Current.Value;
        Current.Value = new ContextState(mode, trafficIngressObserver);
        return new Scope(previous);
    }

    private sealed class ContextState(
        FneTransportEncryptionMode mode,
        Action<long>? trafficIngressObserver)
    {
        public FneTransportEncryptionMode Mode { get; } = mode;
        public Action<long>? TrafficIngressObserver { get; } = trafficIngressObserver;
        public int ReceiverCount { get; set; }
    }

    private sealed class Scope(ContextState? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            Current.Value = previous;
            disposed = true;
        }
    }
}

public struct UdpFrame
{
    public IPEndPoint Endpoint;
    public byte[] Message;
}

public abstract class UdpBase
{
    private const ushort AesWrappedPacketMagic = 0xC0FE;
    private const int AesBlockBytes = 16;

    protected readonly UdpClient client;

    private readonly object cryptoSync = new();
    private readonly FneTransportEncryptionMode configuredMode;
    private readonly FneUdpChannelKind channelKind;
    private readonly Action<long>? trafficIngressObserver;
    private readonly InboundReplayCache replayCache = new();
    private FneTransportEncryptionMode sendMode;
    private FneTransportEncryptionMode lastSentMode;
    private FneTransportEncryptionMode? negotiatedMode;
    private bool isCryptoWrapped;
    private byte[]? presharedKey;

    protected UdpBase()
    {
        client = new UdpClient();
        (configuredMode, channelKind, trafficIngressObserver) =
            FneTransportEncryptionContext.Capture();
        sendMode = InitialMode(configuredMode);
        lastSentMode = sendMode;
    }

    public FneTransportEncryptionMode ConfiguredEncryptionMode => configuredMode;

    public FneTransportEncryptionMode? NegotiatedEncryptionMode
    {
        get
        {
            lock (cryptoSync)
                return negotiatedMode;
        }
    }

    public void SetPresharedKey(byte[]? key)
    {
        lock (cryptoSync)
        {
            presharedKey = key?.ToArray();
            isCryptoWrapped = key is not null;
            sendMode = InitialMode(configuredMode);
            lastSentMode = sendMode;
            negotiatedMode = configuredMode == FneTransportEncryptionMode.Auto
                ? null
                : configuredMode;
            replayCache.Clear();
        }
    }

    public async Task<UdpFrame> Receive()
    {
        while (true)
        {
            UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
            if (channelKind == FneUdpChannelKind.Traffic && trafficIngressObserver is not null)
            {
                try
                {
                    trafficIngressObserver(Stopwatch.GetTimestamp());
                }
                catch
                {
                    // Timing observation is diagnostic only and must not stop
                    // the protocol receiver.
                }
            }
            if (!FneInboundFramePolicy.AcceptsInbound(channelKind))
                continue;

            byte[] message = result.Buffer;
            byte[]? key;
            bool wrapped;
            FneTransportEncryptionMode preferredMode;

            lock (cryptoSync)
            {
                wrapped = isCryptoWrapped;
                key = presharedKey;
                preferredMode = negotiatedMode ?? lastSentMode;
            }

            if (wrapped)
            {
                if (key is null)
                    throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");

                message = Unwrap(result.Buffer, key, preferredMode);
            }

            if (!FneInboundFramePolicy.ShouldDeliverTraffic(message))
                continue;
            if (wrapped && !replayCache.TryRemember(result.Buffer))
                continue;

            return new UdpFrame
            {
                Message = message,
                Endpoint = result.RemoteEndPoint
            };
        }
    }

    protected byte[] WrapForSend(byte[] message)
    {
        byte[]? key;
        bool wrapped;
        FneTransportEncryptionMode mode;

        lock (cryptoSync)
        {
            wrapped = isCryptoWrapped;
            key = presharedKey;
            mode = negotiatedMode ?? sendMode;
            lastSentMode = mode;
            if (configuredMode == FneTransportEncryptionMode.Auto && negotiatedMode is null)
                sendMode = OtherMode(mode);
        }

        if (!wrapped)
            return message;
        if (key is null)
            throw new InvalidOperationException("Encrypted FNE transport has no preshared key.");

        return mode switch
        {
            FneTransportEncryptionMode.Cbc => WrapCbc(message, key),
            _ => WrapEcb(message, key)
        };
    }

    private byte[] Unwrap(
        byte[] wire,
        byte[] key,
        FneTransportEncryptionMode preferredMode)
    {
        if (wire.Length < 2 || FneUtils.ToUInt16(wire, 0) != AesWrappedPacketMagic)
            return [];

        if (configuredMode != FneTransportEncryptionMode.Auto)
            return TryDecrypt(wire, key, configuredMode, out byte[] decrypted) ? decrypted : [];

        FneTransportEncryptionMode alternateMode = OtherMode(preferredMode);
        foreach (FneTransportEncryptionMode mode in new[] { preferredMode, alternateMode })
        {
            if (!TryDecrypt(wire, key, mode, out byte[] candidate) || !LooksLikeFneFrame(candidate))
                continue;

            lock (cryptoSync)
            {
                negotiatedMode = mode;
                sendMode = mode;
                lastSentMode = mode;
            }

            return candidate;
        }

        return [];
    }

    private static byte[] WrapEcb(byte[] message, byte[] key)
    {
        byte[] padded = PadToBlock(message);
        byte[] encrypted = Transform(padded, key, CipherMode.ECB, encrypt: true);
        return BuildWirePacket(encrypted, null);
    }

    private static byte[] WrapCbc(byte[] message, byte[] key)
    {
        byte[] padded = PadToBlock(message);
        byte[] iv = RandomNumberGenerator.GetBytes(AesBlockBytes);
        byte[] encrypted = Transform(padded, key, CipherMode.CBC, encrypt: true, iv);
        return BuildWirePacket(encrypted, iv);
    }

    private static bool TryDecrypt(
        byte[] wire,
        byte[] key,
        FneTransportEncryptionMode mode,
        out byte[] decrypted)
    {
        decrypted = [];
        try
        {
            if (mode == FneTransportEncryptionMode.Cbc)
            {
                int encryptedLength = wire.Length - 2 - AesBlockBytes;
                if (encryptedLength < AesBlockBytes || encryptedLength % AesBlockBytes != 0)
                    return false;

                byte[] encrypted = wire.AsSpan(2, encryptedLength).ToArray();
                byte[] iv = wire.AsSpan(wire.Length - AesBlockBytes, AesBlockBytes).ToArray();
                decrypted = Transform(encrypted, key, CipherMode.CBC, encrypt: false, iv);
                return true;
            }

            int ecbLength = wire.Length - 2;
            if (ecbLength < AesBlockBytes || ecbLength % AesBlockBytes != 0)
                return false;

            decrypted = Transform(
                wire.AsSpan(2, ecbLength).ToArray(),
                key,
                CipherMode.ECB,
                encrypt: false);
            return true;
        }
        catch (CryptographicException)
        {
            decrypted = [];
            return false;
        }
    }

    private static byte[] Transform(
        byte[] input,
        byte[] key,
        CipherMode mode,
        bool encrypt,
        byte[]? iv = null)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.BlockSize = 128;
        aes.Mode = mode;
        aes.Padding = PaddingMode.None;
        if (mode == CipherMode.CBC)
            aes.IV = iv ?? throw new ArgumentNullException(nameof(iv));

        using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(input, 0, input.Length);
    }

    private static byte[] PadToBlock(byte[] message)
    {
        int paddedLength = ((message.Length + AesBlockBytes - 1) / AesBlockBytes) * AesBlockBytes;
        if (paddedLength == message.Length)
            return message.ToArray();

        byte[] padded = new byte[paddedLength];
        Buffer.BlockCopy(message, 0, padded, 0, message.Length);
        return padded;
    }

    private static byte[] BuildWirePacket(byte[] encrypted, byte[]? iv)
    {
        byte[] wire = new byte[2 + encrypted.Length + (iv?.Length ?? 0)];
        FneUtils.WriteBytes(AesWrappedPacketMagic, ref wire, 0);
        Buffer.BlockCopy(encrypted, 0, wire, 2, encrypted.Length);
        if (iv is not null)
            Buffer.BlockCopy(iv, 0, wire, 2 + encrypted.Length, iv.Length);
        return wire;
    }

    private static bool LooksLikeFneFrame(byte[] message)
    {
        if (message.Length < Constants.RtpHeaderLengthBytes + Constants.RtpExtensionHeaderLengthBytes)
            return false;

        bool versionTwo = ((message[0] >> 6) & 0x03) == 0x02;
        bool hasExtension = (message[0] & 0x10) != 0;
        byte payloadType = (byte)(message[1] & 0x7F);
        return versionTwo && hasExtension &&
            payloadType is Constants.DVMRtpPayloadType or Constants.DVMRtpPayloadType + 1;
    }

    private static FneTransportEncryptionMode InitialMode(FneTransportEncryptionMode mode)
        => mode == FneTransportEncryptionMode.Cbc
            ? FneTransportEncryptionMode.Cbc
            : FneTransportEncryptionMode.Ecb;

    private static FneTransportEncryptionMode OtherMode(FneTransportEncryptionMode mode)
        => mode == FneTransportEncryptionMode.Cbc
            ? FneTransportEncryptionMode.Ecb
            : FneTransportEncryptionMode.Cbc;

    private sealed class InboundReplayCache
    {
        private const int MaximumEntries = 4_096;
        private readonly object sync = new();
        private readonly HashSet<string> fingerprints = new(StringComparer.Ordinal);
        private readonly Queue<string> insertionOrder = new();

        public bool TryRemember(byte[] wire)
        {
            string fingerprint = Convert.ToBase64String(SHA256.HashData(wire));
            lock (sync)
            {
                if (!fingerprints.Add(fingerprint))
                    return false;

                insertionOrder.Enqueue(fingerprint);
                while (insertionOrder.Count > MaximumEntries)
                    fingerprints.Remove(insertionOrder.Dequeue());
                return true;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                fingerprints.Clear();
                insertionOrder.Clear();
            }
        }
    }
}

internal static class FneInboundFramePolicy
{
    private const int HeaderLength = 32;

    public static bool AcceptsInbound(FneUdpChannelKind channelKind)
        => channelKind == FneUdpChannelKind.Traffic;

    public static bool ShouldDeliverTraffic(byte[] message)
    {
        if (message.Length < HeaderLength ||
            !LooksLikeCompleteHeader(message))
        {
            return false;
        }

        uint declaredLength = ReadUInt32(message.AsSpan(28, 4));
        int availableLength = message.Length - HeaderLength;
        if (declaredLength == 0 || declaredLength > availableLength)
            return false;

        ReadOnlySpan<byte> payload = message.AsSpan(HeaderLength, checked((int)declaredLength));
        byte function = message[18];
        byte subFunction = message[19];
        return HasSafePayload(function, subFunction, payload);
    }

    private static bool LooksLikeCompleteHeader(ReadOnlySpan<byte> message)
    {
        bool versionTwo = ((message[0] >> 6) & 0x03) == 0x02;
        bool hasExtension = (message[0] & 0x10) != 0;
        bool hasNoCsrcEntries = (message[0] & 0x0F) == 0;
        byte payloadType = (byte)(message[1] & 0x7F);
        bool fneExtension = message[12] == 0x00 &&
            message[13] == Constants.DVMFrameStart &&
            message[14] == 0x00 &&
            message[15] == Constants.RtpFNEHeaderExtLength;
        return versionTwo && hasExtension && hasNoCsrcEntries && fneExtension &&
            payloadType is Constants.DVMRtpPayloadType or Constants.DVMRtpPayloadType + 1;
    }

    private static bool HasSafePayload(
        byte function,
        byte subFunction,
        ReadOnlySpan<byte> payload)
        => function switch
        {
            Constants.NET_FUNC_PROTOCOL => subFunction switch
            {
                Constants.NET_PROTOCOL_SUBFUNC_DMR => payload.Length >= 16,
                Constants.NET_PROTOCOL_SUBFUNC_P25 => payload.Length >= 23,
                Constants.NET_PROTOCOL_SUBFUNC_NXDN => payload.Length >= 16,
                Constants.NET_PROTOCOL_SUBFUNC_ANALOG => payload.Length >= 16,
                _ => true
            },
            Constants.NET_FUNC_MASTER => subFunction switch
            {
                Constants.NET_MASTER_SUBFUNC_ACTIVE_TGS => false,
                Constants.NET_MASTER_SUBFUNC_DEACTIVE_TGS => false,
                Constants.NET_MASTER_SUBFUNC_HA_PARAMS => HasSafeHaPayload(payload),
                _ => true
            },
            Constants.NET_FUNC_INCALL_CTRL => subFunction == Constants.NET_PROTOCOL_SUBFUNC_DMR
                ? payload.Length >= 15
                : payload.Length >= 14,
            Constants.NET_FUNC_KEY_RSP => HasSafeKeyResponse(payload),
            Constants.NET_FUNC_ACK => payload.Length >= 10,
            Constants.NET_FUNC_NAK => payload.Length <= 10 || payload.Length >= 12,
            _ => true
        };

    private static bool HasSafeHaPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            return false;

        uint announcedBytes = ReadUInt32(payload.Slice(6, 4));
        uint entryBytes = Constants.HAParamsEntryLen;
        uint entries = entryBytes == 0 ? 0 : announcedBytes / entryBytes;
        ulong requiredLength = 10UL + (ulong)entries * entryBytes;
        return requiredLength <= (ulong)payload.Length;
    }

    private static bool HasSafeKeyResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
            return false;

        ReadOnlySpan<byte> kmm = payload[11..];
        if (kmm[0] != (byte)P25.KMM.KmmMessageType.MODIFY_KEY_CMD)
            return true;
        if (kmm.Length < 18)
            return false;

        int offset = 14;
        if (kmm[10] == P25.P25Defines.KMM_DECRYPT_INSTRUCTION_MI)
            offset += P25.P25Defines.P25_MI_LENGTH;
        if (offset > kmm.Length - 4)
            return false;

        int keyLength = kmm[offset + 2];
        int keyCount = kmm[offset + 3];
        offset += 4;
        for (int index = 0; index < keyCount; index++)
        {
            if (offset > kmm.Length - 5)
                return false;

            int keyNameLength = kmm[offset] & 0x1F;
            int entryLength = 5 + keyNameLength + keyLength;
            if (entryLength > kmm.Length - offset)
                return false;
            offset += entryLength;
        }

        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> value)
        => ((uint)value[0] << 24) |
            ((uint)value[1] << 16) |
            ((uint)value[2] << 8) |
            value[3];
}

public sealed class UdpReceiver : UdpBase
{
    private IPEndPoint? endpoint;
    private bool connected;

    public IPEndPoint? EndPoint => endpoint;

    public void Connect(string hostName, int port)
    {
        try
        {
            if (!IPAddress.TryParse(hostName, out IPAddress? address))
                address = Dns.GetHostAddresses(hostName).FirstOrDefault();
            if (address is null)
                return;

            Connect(new IPEndPoint(address, port));
        }
        catch (SocketException)
        {
            return;
        }
    }

    public void Connect(IPEndPoint destination)
    {
        endpoint = destination;
        client.Connect(destination.Address.ToString(), destination.Port);
        connected = true;
    }

    public void Send(UdpFrame frame)
    {
        frame.Message = WrapForSend(frame.Message);
        if (connected)
            client.Send(frame.Message, frame.Message.Length);
        else
            client.Send(frame.Message, frame.Message.Length, frame.Endpoint);
    }
}
