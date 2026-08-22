#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;

namespace fnecore;

internal static class FneTransportCryptoCodec
{
    private const ushort AesWrappedPacketMagic = 0xC0FE;
    private const int AesBlockBytes = 16;

    public static byte[] Wrap(
        byte[] message,
        byte[] key,
        FneTransportEncryptionMode mode)
        => mode == FneTransportEncryptionMode.Cbc
            ? WrapCbc(message, key)
            : WrapEcb(message, key);

    public static bool TryUnwrap(
        byte[] wire,
        byte[] key,
        FneTransportEncryptionMode mode,
        out byte[] decrypted)
    {
        decrypted = [];
        if (wire.Length < 2 || FneUtils.ToUInt16(wire, 0) != AesWrappedPacketMagic)
            return false;

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
}
