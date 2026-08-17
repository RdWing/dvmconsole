using System.Globalization;
using System.Security.Cryptography;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Media;

public interface IDmrKeyResolver
{
    bool TryResolve(string systemName, byte algorithmId, byte keyId, out ReadOnlyMemory<byte> key);
    bool CanResolve(string systemName, string? algorithm, string? keyId);
}

// System-scoped local DMR key lookup. P25 KMM material remains deliberately
// separate because the FNE KMM path distributes P25 key identifiers.
public sealed class DmrKeyRing : IDmrKeyResolver, IDisposable
{
    private readonly Dictionary<(string SystemName, byte AlgorithmId, byte KeyId), byte[]> keys = [];
    private bool disposed;

    public DmrKeyRing()
    {
    }

    public DmrKeyRing(string systemName, KeyContainer container)
    {
        AddLocalKeys(systemName, container);
    }

    public void AddLocalKeys(string systemName, KeyContainer container)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(container);
        string scope = NormalizeSystemName(systemName);
        foreach (KeyEntry entry in container.Keys ?? [])
        {
            if (!string.Equals(entry.Protocol, "dmr", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.KeyId is 0 or > byte.MaxValue)
                throw new FormatException("DMR key IDs must be between 1 and 255.");
            if (entry.AlgId is < byte.MinValue or > byte.MaxValue)
                throw new FormatException($"DMR algorithm ID {entry.AlgId} is outside the supported byte range.");

            byte algorithmId = (byte)entry.AlgId;
            int expectedLength;
            try
            {
                expectedLength = DmrPrivacyAlgorithms.KeyBytes(algorithmId);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new FormatException($"Unsupported DMR encryption algorithm 0x{algorithmId:X2}.", exception);
            }

            byte[] material = entry.KeyBytes;
            if (material.Length != expectedLength)
            {
                throw new FormatException(
                    $"DMR algorithm 0x{algorithmId:X2} requires exactly {expectedLength} bytes of key material; received {material.Length}.");
            }

            var lookup = (scope, algorithmId, (byte)entry.KeyId);
            if (!keys.TryAdd(lookup, material))
                throw new FormatException($"Duplicate DMR key 0x{entry.KeyId:X2} for algorithm 0x{algorithmId:X2}.");
        }
    }

    public bool TryResolve(string systemName, byte algorithmId, byte keyId, out ReadOnlyMemory<byte> key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keys.TryGetValue((NormalizeSystemName(systemName), algorithmId, keyId), out byte[]? material))
        {
            key = material.ToArray();
            return true;
        }
        key = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    public bool CanResolve(string systemName, string? algorithm, string? keyId)
        => TryParseAlgorithmId(algorithm, out byte algorithmId) &&
            TryParseKeyId(keyId, out byte parsedKeyId) &&
            TryResolve(systemName, algorithmId, parsedKeyId, out _);

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (byte[] material in keys.Values)
            CryptographicOperations.ZeroMemory(material);
        keys.Clear();
        disposed = true;
    }

    public static bool TryParseAlgorithmId(string? value, out byte algorithmId)
    {
        algorithmId = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string normalized = value.Trim();
        if (normalized.Equals("arc4", StringComparison.OrdinalIgnoreCase))
            algorithmId = DmrPrivacyAlgorithms.Arc4;
        else if (normalized.Equals("des", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("des-ofb", StringComparison.OrdinalIgnoreCase))
            algorithmId = DmrPrivacyAlgorithms.DesOfb;
        else if (normalized.Equals("aes", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("aes-256", StringComparison.OrdinalIgnoreCase))
            algorithmId = DmrPrivacyAlgorithms.Aes256;
        else if (!TryParseByte(normalized, out algorithmId))
            return false;

        return algorithmId is DmrPrivacyAlgorithms.Arc4 or
            DmrPrivacyAlgorithms.DesOfb or
            DmrPrivacyAlgorithms.Aes256;
    }

    public static bool TryParseKeyId(string? value, out byte keyId)
    {
        keyId = 0;
        return !string.IsNullOrWhiteSpace(value) &&
            TryParseByte(value.Trim(), out keyId) &&
            keyId != 0;
    }

    private static bool TryParseByte(string value, out byte result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(
                value[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out result);
        }
        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static string NormalizeSystemName(string? systemName)
        => (systemName ?? string.Empty).Trim().ToUpperInvariant();
}
