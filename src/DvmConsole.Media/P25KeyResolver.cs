using System.Globalization;
using DvmConsole.Core.Configuration;
using fnecore.P25;

namespace DvmConsole.Media;

// Resolves P25 traffic-encryption keys without exposing the key-file model to
// the receive session or desktop layer.
public interface IP25KeyResolver
{
    bool TryResolve(byte algorithmId, ushort keyId, out ReadOnlyMemory<byte> key);

    bool CanResolve(string? algorithm, string? keyId);
}

// Mutable in-memory lookup of P25 AES, DES-OFB, and ARC4/ADP key material.
// The codeplug key file is the initial seed; runtime KMM responses can add or
// replace entries without persisting key material.
public sealed class P25KeyRing : IP25KeyResolver
{
    private readonly object sync = new();
    private readonly Dictionary<(byte AlgorithmId, ushort KeyId), byte[]> keys;

    public P25KeyRing(KeyContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        keys = [];
        foreach (KeyEntry entry in container.Keys ?? [])
        {
            if (entry.KeyId == 0 || entry.AlgId is < byte.MinValue or > byte.MaxValue)
                continue;

            byte algorithmId = (byte)entry.AlgId;
            if (!IsSupportedAlgorithm(algorithmId))
                continue;

            byte[] key = entry.KeyBytes;
            if (key.Length == 0)
                continue;
            if (algorithmId == P25Defines.P25_ALGO_AES && key.Length > 32)
                throw new FormatException("P25 AES key material cannot exceed 32 bytes.");

            keys[(algorithmId, entry.KeyId)] = (byte[])key.Clone();
        }
    }

    public void AddOrReplace(byte algorithmId, ushort keyId, ReadOnlySpan<byte> key)
    {
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId), "P25 key ID must be non-zero.");
        if (!IsSupportedAlgorithm(algorithmId))
            throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported P25 encryption algorithm.");
        if (key.Length == 0)
            throw new ArgumentException("P25 key material cannot be empty.", nameof(key));
        if (algorithmId == P25Defines.P25_ALGO_AES && key.Length > 32)
            throw new ArgumentException("P25 AES key material cannot exceed 32 bytes.", nameof(key));

        lock (sync)
            keys[(algorithmId, keyId)] = key.ToArray();
    }

    public bool TryResolve(byte algorithmId, ushort keyId, out ReadOnlyMemory<byte> key)
    {
        lock (sync)
        {
            if (keyId != 0 && keys.TryGetValue((algorithmId, keyId), out byte[]? material))
            {
                key = new ReadOnlyMemory<byte>((byte[])material.Clone());
                return true;
            }
        }

        key = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    public bool CanResolve(string? algorithm, string? keyId)
    {
        return TryParseAlgorithmId(algorithm, out byte algorithmId) &&
            TryParseKeyId(keyId, out ushort parsedKeyId) &&
            TryResolve(algorithmId, parsedKeyId, out _);
    }

    public static bool TryParseAlgorithmId(string? value, out byte algorithmId)
    {
        algorithmId = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        if (normalized.Equals("aes", StringComparison.OrdinalIgnoreCase))
            algorithmId = P25Defines.P25_ALGO_AES;
        else if (normalized.Equals("des", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("des-ofb", StringComparison.OrdinalIgnoreCase))
            algorithmId = P25Defines.P25_ALGO_DES;
        else if (normalized.Equals("arc4", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("adp", StringComparison.OrdinalIgnoreCase))
            algorithmId = P25Defines.P25_ALGO_ARC4;
        else if (!TryParseByte(normalized, out algorithmId))
            return false;

        return IsSupportedAlgorithm(algorithmId);
    }

    public static bool TryParseKeyId(string? value, out ushort keyId)
    {
        keyId = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        NumberStyles style = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? NumberStyles.AllowHexSpecifier
            : NumberStyles.Integer;
        if (style == NumberStyles.AllowHexSpecifier)
            normalized = normalized[2..];

        if (!ushort.TryParse(normalized, style, CultureInfo.InvariantCulture, out keyId) || keyId == 0)
            return false;

        return true;
    }

    private static bool IsSupportedAlgorithm(byte algorithmId)
    {
        return algorithmId is P25Defines.P25_ALGO_AES or
            P25Defines.P25_ALGO_DES or
            P25Defines.P25_ALGO_ARC4;
    }

    private static bool TryParseByte(string value, out byte result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
