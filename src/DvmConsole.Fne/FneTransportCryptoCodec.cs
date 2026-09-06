// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#nullable enable

using System.Security.Cryptography;
using System.Buffers;

namespace fnecore;

internal static class FneTransportCryptoCodec
{
    public static byte[] Wrap(
        byte[] message,
        byte[] key,
        FneTransportEncryptionMode mode)
    {
        using var context = new FneTransportCryptoContext(key);
        return context.Wrap(message, mode);
    }

    public static bool TryUnwrap(
        byte[] wire,
        byte[] key,
        FneTransportEncryptionMode mode,
        out byte[] decrypted)
    {
        using var context = new FneTransportCryptoContext(key);
        return context.TryUnwrap(wire, mode, out decrypted);
    }

    public static bool LooksLikeFneFrame(byte[] message)
    {
        if (message.Length < Constants.RtpHeaderLengthBytes + Constants.RtpExtensionHeaderLengthBytes)
            return false;

        bool versionTwo = ((message[0] >> 6) & 0x03) == 0x02;
        bool hasExtension = (message[0] & 0x10) != 0;
        byte payloadType = (byte)(message[1] & 0x7F);
        return versionTwo && hasExtension &&
            payloadType is Constants.DVMRtpPayloadType or Constants.DVMRtpPayloadType + 1;
    }

}

// Owns configured AES state for one FNE UDP session. Wire buffers remain
// caller-owned results, while padding uses a bounded shared rental.
internal sealed class FneTransportCryptoContext : IDisposable
{
    private const ushort AesWrappedPacketMagic = 0xC0FE;
    private const int AesBlockBytes = 16;
    private readonly object sync = new();
    private readonly byte[] key;
    private readonly Aes aes;
    private bool disposed;

    public FneTransportCryptoContext(ReadOnlySpan<byte> key)
    {
        this.key = key.ToArray();
        aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Key = this.key;
    }

    public byte[] Wrap(ReadOnlySpan<byte> message, FneTransportEncryptionMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int paddedLength = checked((message.Length + AesBlockBytes - 1) / AesBlockBytes * AesBlockBytes);
        int ivLength = mode == FneTransportEncryptionMode.Cbc ? AesBlockBytes : 0;
        byte[] wire = new byte[checked(2 + paddedLength + ivLength)];
        FneUtils.WriteBytes(AesWrappedPacketMagic, ref wire, 0);

        byte[] rented = ArrayPool<byte>.Shared.Rent(paddedLength);
        try
        {
            Span<byte> padded = rented.AsSpan(0, paddedLength);
            padded.Clear();
            message.CopyTo(padded);
            Span<byte> encrypted = wire.AsSpan(2, paddedLength);
            lock (sync)
            {
                if (mode == FneTransportEncryptionMode.Cbc)
                {
                    Span<byte> iv = wire.AsSpan(2 + paddedLength, AesBlockBytes);
                    RandomNumberGenerator.Fill(iv);
                    aes.EncryptCbc(padded, iv, encrypted, PaddingMode.None);
                }
                else
                {
                    aes.EncryptEcb(padded, encrypted, PaddingMode.None);
                }
            }
            return wire;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, paddedLength));
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public bool TryUnwrap(
        ReadOnlySpan<byte> wire,
        FneTransportEncryptionMode mode,
        out byte[] decrypted)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        decrypted = [];
        if (wire.Length < 2 || wire[0] != 0xC0 || wire[1] != 0xFE)
            return false;

        int ivLength = mode == FneTransportEncryptionMode.Cbc ? AesBlockBytes : 0;
        int encryptedLength = wire.Length - 2 - ivLength;
        if (encryptedLength < AesBlockBytes || encryptedLength % AesBlockBytes != 0)
            return false;

        byte[] candidate = new byte[encryptedLength];
        try
        {
            ReadOnlySpan<byte> encrypted = wire.Slice(2, encryptedLength);
            lock (sync)
            {
                if (mode == FneTransportEncryptionMode.Cbc)
                {
                    ReadOnlySpan<byte> iv = wire.Slice(2 + encryptedLength, AesBlockBytes);
                    aes.DecryptCbc(encrypted, iv, candidate, PaddingMode.None);
                }
                else
                {
                    aes.DecryptEcb(encrypted, candidate, PaddingMode.None);
                }
            }
            decrypted = candidate;
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            aes.Dispose();
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
