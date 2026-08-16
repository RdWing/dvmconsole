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

namespace fnecore;

public enum FneTransportEncryptionMode
{
    Auto,
    Ecb,
    Cbc
}

// FnePeer constructs its UDP transports internally. This short-lived ambient
// scope lets each peer capture an independent transport mode without changing
// the upstream FnePeer API or maintaining a private fnecore fork.
public static class FneTransportEncryptionContext
{
    private static readonly AsyncLocal<FneTransportEncryptionMode?> CurrentMode = new();

    internal static FneTransportEncryptionMode CaptureMode()
        => CurrentMode.Value ?? FneTransportEncryptionMode.Auto;

    public static IDisposable Use(FneTransportEncryptionMode mode)
    {
        FneTransportEncryptionMode? previous = CurrentMode.Value;
        CurrentMode.Value = mode;
        return new Scope(previous);
    }

    private sealed class Scope(FneTransportEncryptionMode? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            CurrentMode.Value = previous;
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
    private FneTransportEncryptionMode sendMode;
    private FneTransportEncryptionMode lastSentMode;
    private FneTransportEncryptionMode? negotiatedMode;
    private bool isCryptoWrapped;
    private byte[]? presharedKey;

    protected UdpBase()
    {
        client = new UdpClient();
        configuredMode = FneTransportEncryptionContext.CaptureMode();
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
        }
    }

    public async Task<UdpFrame> Receive()
    {
        UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
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

        return new UdpFrame
        {
            Message = message,
            Endpoint = result.RemoteEndPoint
        };
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
