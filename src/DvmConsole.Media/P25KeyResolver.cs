using System.Globalization;
using System.Security.Cryptography;
using DvmConsole.Core.Configuration;
using fnecore.P25;

namespace DvmConsole.Media;

// Resolves P25 traffic-encryption keys without exposing the key-file model to
// the receive session or desktop layer.
public interface IP25KeyResolver
{
    bool TryResolve(string systemName, byte algorithmId, ushort keyId, out ReadOnlyMemory<byte> key);

    bool CanResolve(string systemName, string? algorithm, string? keyId);

    bool TryGetSource(string systemName, byte algorithmId, ushort keyId, out P25KeyMaterialSource source);
}

public enum P25KeyMaterialSource
{
    LocalFile,
    FneKmm
}

// Mutable in-memory lookup of P25 AES, DES-OFB, and ARC4/ADP key material.
// Each system has two layers: FNE/KMM material takes precedence while it is
// connected, with the local key file retained as an automatic fallback.
public sealed class P25KeyRing : IP25KeyResolver, IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<(string SystemName, byte AlgorithmId, ushort KeyId), KeySlot> keys = [];
    private bool disposed;

    public P25KeyRing()
    {
    }

    public P25KeyRing(string systemName, KeyContainer container)
    {
        AddLocalKeys(systemName, container);
    }

    public void AddLocalKeys(string systemName, KeyContainer container)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string scope = NormalizeSystemName(systemName);
        ArgumentNullException.ThrowIfNull(container);

        var loaded = new Dictionary<(byte AlgorithmId, ushort KeyId), byte[]>();
        foreach (KeyEntry entry in container.Keys ?? [])
        {
            if (entry.KeyId == 0)
                throw new FormatException("P25 key IDs must be non-zero.");
            if (entry.AlgId is < byte.MinValue or > byte.MaxValue)
                throw new FormatException($"P25 algorithm ID {entry.AlgId} is outside the supported byte range.");

            byte algorithmId = (byte)entry.AlgId;
            if (!IsSupportedAlgorithm(algorithmId))
                throw new FormatException($"Unsupported P25 encryption algorithm 0x{algorithmId:X2}.");

            byte[] key = NormalizeKeyMaterial(
                algorithmId,
                entry.KeyBytes,
                static message => new FormatException(message));
            if (!loaded.TryAdd((algorithmId, entry.KeyId), key))
                throw new FormatException($"Duplicate P25 key 0x{entry.KeyId:X4} for algorithm 0x{algorithmId:X2}.");
        }

        lock (sync)
        {
            foreach (var entry in keys.Where(entry => entry.Key.SystemName == scope).ToArray())
            {
                ClearMaterial(ref entry.Value.LocalMaterial);
                if (entry.Value.FneMaterial is null)
                    keys.Remove(entry.Key);
            }

            foreach (((byte algorithmId, ushort keyId), byte[] key) in loaded)
            {
                var lookup = (scope, algorithmId, keyId);
                if (!keys.TryGetValue(lookup, out KeySlot? slot))
                    keys[lookup] = slot = new KeySlot();
                slot.LocalMaterial = (byte[])key.Clone();
            }
        }
    }

    public void AddOrReplaceFromFne(string systemName, byte algorithmId, ushort keyId, ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId), "P25 key ID must be non-zero.");
        if (!IsSupportedAlgorithm(algorithmId))
            throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported P25 encryption algorithm.");
        byte[] normalizedKey = NormalizeKeyMaterial(
            algorithmId,
            key,
            static message => new ArgumentException(message, "key"));

        lock (sync)
        {
            var lookup = (NormalizeSystemName(systemName), algorithmId, keyId);
            if (!keys.TryGetValue(lookup, out KeySlot? slot))
                keys[lookup] = slot = new KeySlot();
            ClearMaterial(ref slot.FneMaterial);
            slot.FneMaterial = normalizedKey;
        }
    }

    public bool TryResolve(string systemName, byte algorithmId, ushort keyId, out ReadOnlyMemory<byte> key)
    {
        lock (sync)
        {
            if (keyId != 0 && keys.TryGetValue(
                    (NormalizeSystemName(systemName), algorithmId, keyId),
                    out KeySlot? slot))
            {
                byte[]? material = slot.FneMaterial ?? slot.LocalMaterial;
                if (material is not null)
                {
                    key = new ReadOnlyMemory<byte>((byte[])material.Clone());
                    return true;
                }
            }
        }

        key = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    public bool CanResolve(string systemName, string? algorithm, string? keyId)
    {
        return TryParseAlgorithmId(algorithm, out byte algorithmId) &&
            TryParseKeyId(keyId, out ushort parsedKeyId) &&
            TryResolve(systemName, algorithmId, parsedKeyId, out _);
    }

    public bool TryGetSource(
        string systemName,
        byte algorithmId,
        ushort keyId,
        out P25KeyMaterialSource source)
    {
        lock (sync)
        {
            if (keys.TryGetValue(
                    (NormalizeSystemName(systemName), algorithmId, keyId),
                    out KeySlot? slot))
            {
                if (slot.FneMaterial is not null)
                {
                    source = P25KeyMaterialSource.FneKmm;
                    return true;
                }
                if (slot.LocalMaterial is not null)
                {
                    source = P25KeyMaterialSource.LocalFile;
                    return true;
                }
            }
        }

        source = default;
        return false;
    }

    public void ClearFneKeys(string systemName)
    {
        string scope = NormalizeSystemName(systemName);
        lock (sync)
        {
            foreach (var entry in keys.Where(entry => entry.Key.SystemName == scope).ToArray())
            {
                ClearMaterial(ref entry.Value.FneMaterial);
                if (entry.Value.LocalMaterial is null)
                    keys.Remove(entry.Key);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        lock (sync)
        {
            foreach (KeySlot slot in keys.Values)
            {
                ClearMaterial(ref slot.FneMaterial);
                ClearMaterial(ref slot.LocalMaterial);
            }
            keys.Clear();
            disposed = true;
        }
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

    private static byte[] NormalizeKeyMaterial(
        byte algorithmId,
        ReadOnlySpan<byte> material,
        Func<string, Exception> createException)
    {
        int expectedLength = algorithmId switch
        {
            P25Defines.P25_ALGO_AES => 32,
            P25Defines.P25_ALGO_DES => 8,
            P25Defines.P25_ALGO_ARC4 => 5,
            _ => 0
        };

        if (algorithmId == P25Defines.P25_ALGO_AES)
        {
            if (material.IsEmpty || material.Length > expectedLength)
            {
                throw createException(
                    $"P25 AES key material must contain 1 to {expectedLength} bytes; received {material.Length}.");
            }

            // Legacy P25Crypto accepts short AES material and appends zero
            // bytes until it reaches the 32-byte AES-256 key size.
            byte[] normalized = new byte[expectedLength];
            material.CopyTo(normalized);
            return normalized;
        }

        if (material.Length != expectedLength)
        {
            throw createException(
                $"P25 algorithm 0x{algorithmId:X2} requires exactly {expectedLength} bytes of key material; received {material.Length}.");
        }

        return material.ToArray();
    }

    private static void ClearMaterial(ref byte[]? material)
    {
        if (material is not null)
            CryptographicOperations.ZeroMemory(material);
        material = null;
    }

    private static string NormalizeSystemName(string? systemName)
        => (systemName ?? string.Empty).Trim().ToUpperInvariant();

    private static bool TryParseByte(string value, out byte result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private sealed class KeySlot
    {
        public byte[]? LocalMaterial;
        public byte[]? FneMaterial;
    }
}
