using System.Globalization;
using System.Security.Cryptography;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Media;

public interface INxdnKeyResolver
{
    bool TryResolve(string systemName, byte algorithmId, byte keyId, out ReadOnlyMemory<byte> key);
    bool CanResolve(string systemName, string? algorithm, string? keyId);
}

public sealed class NxdnKeyRing : INxdnKeyResolver, IDisposable
{
    private readonly Dictionary<(string System, byte Algorithm, byte KeyId), byte[]> keys = [];
    private bool disposed;

    public NxdnKeyRing() { }
    public NxdnKeyRing(string systemName, KeyContainer container) => AddLocalKeys(systemName, container);

    public void AddLocalKeys(string systemName, KeyContainer container)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(container);
        string scope = Normalize(systemName);
        foreach (KeyEntry entry in container.Keys ?? [])
        {
            if (!string.Equals(entry.Protocol, "nxdn", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.KeyId is 0 or > 63)
                throw new FormatException("NXDN key IDs must be between 1 and 63.");
            if (entry.AlgId is < 1 or > 3)
                throw new FormatException($"Unsupported NXDN cipher type {entry.AlgId}.");
            byte algorithm = (byte)entry.AlgId;
            byte[] material = entry.KeyBytes;
            int expected = NxdnPrivacyAlgorithms.KeyBytes(algorithm);
            if (material.Length != expected)
                throw new FormatException($"NXDN cipher type {algorithm} requires exactly {expected} key bytes.");
            if (!keys.TryAdd((scope, algorithm, (byte)entry.KeyId), material))
                throw new FormatException($"Duplicate NXDN key {entry.KeyId} for cipher type {algorithm}.");
        }
    }

    public bool TryResolve(string systemName, byte algorithmId, byte keyId, out ReadOnlyMemory<byte> key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keys.TryGetValue((Normalize(systemName), algorithmId, keyId), out byte[]? value))
        {
            key = value.ToArray();
            return true;
        }
        key = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    public bool CanResolve(string systemName, string? algorithm, string? keyId)
        => TryParseAlgorithmId(algorithm, out byte parsedAlgorithm) &&
            TryParseKeyId(keyId, out byte parsedKey) &&
            TryResolve(systemName, parsedAlgorithm, parsedKey, out _);

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (byte[] key in keys.Values)
            CryptographicOperations.ZeroMemory(key);
        keys.Clear();
        disposed = true;
    }

    public static bool TryParseAlgorithmId(string? value, out byte result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string normalized = value.Trim();
        if (normalized.Equals("ehr", StringComparison.OrdinalIgnoreCase) || normalized.Equals("scrambler", StringComparison.OrdinalIgnoreCase))
            result = NxdnPrivacyAlgorithms.Ehr;
        else if (normalized.Equals("des", StringComparison.OrdinalIgnoreCase) || normalized.Equals("des-ofb", StringComparison.OrdinalIgnoreCase))
            result = NxdnPrivacyAlgorithms.Des;
        else if (normalized.Equals("aes", StringComparison.OrdinalIgnoreCase) || normalized.Equals("aes-256", StringComparison.OrdinalIgnoreCase))
            result = NxdnPrivacyAlgorithms.Aes256;
        else if (!TryParseByte(normalized, out result))
            return false;
        return result is >= 1 and <= 3;
    }

    public static bool TryParseKeyId(string? value, out byte result)
    {
        result = 0;
        return !string.IsNullOrWhiteSpace(value) &&
            TryParseByte(value.Trim(), out result) &&
            result is >= 1 and <= 63;
    }

    private static bool TryParseByte(string value, out byte result)
        => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)
            : byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
