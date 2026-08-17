using DvmConsole.Media;

namespace DvmConsole.Desktop;

// Redacted operator-facing encryption status.  It intentionally exposes
// algorithm and key identifiers but never key material.
public sealed record KeyStatusItemViewModel(
    string SystemName,
    string ChannelName,
    string ModeText,
    string AlgorithmIdText,
    string KeyIdText,
    string StatusText)
{
    public static KeyStatusItemViewModel From(
        ChannelViewModel channel,
        IP25KeyResolver? keyResolver,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        bool isDmr = channel.Definition.Mode == "dmr";
        bool isNxdn = channel.Definition.Mode == "nxdn";
        byte algorithmId;
        bool parsedAlgorithm = isDmr
            ? DmrKeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out algorithmId)
            : isNxdn
                ? NxdnKeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out algorithmId)
                : P25KeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out algorithmId);
        string algorithmText = parsedAlgorithm
            ? $"0x{algorithmId:X2}"
            : channel.Definition.EncryptionAlgorithm;
        ushort keyId;
        bool parsedKeyId;
        if (isDmr || isNxdn)
        {
            parsedKeyId = isDmr
                ? DmrKeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out byte protocolKeyId)
                : NxdnKeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out protocolKeyId);
            keyId = protocolKeyId;
        }
        else
        {
            parsedKeyId = P25KeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out keyId);
        }
        string keyIdText = parsedKeyId
            ? isDmr || isNxdn ? $"0x{keyId:X2}" : $"0x{keyId:X4}"
            : channel.Definition.EncryptionKeyId ?? "—";
        bool available = channel.Definition.Mode switch
        {
            "p25" => keyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            "dmr" => dmrKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            "nxdn" => nxdnKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            _ => false
        };
        string statusText = !channel.Definition.IsEncrypted
            ? "Clear"
            : channel.Definition.Mode is not ("p25" or "dmr" or "nxdn")
                ? "Unsupported protocol"
                : available
                    ? channel.Definition.Mode == "p25"
                        ? DescribeAvailableKey(
                            keyResolver!,
                            channel.Definition.SystemName,
                            algorithmId,
                            keyId)
                        : "Available · local file"
                    : "Key unavailable";

        return new KeyStatusItemViewModel(
            channel.Definition.SystemName,
            channel.Name,
            channel.ModeText,
            algorithmText,
            keyIdText,
            statusText);
    }

    private static string DescribeAvailableKey(
        IP25KeyResolver resolver,
        string systemName,
        byte algorithmId,
        ushort keyId)
    {
        if (!resolver.TryGetSource(systemName, algorithmId, keyId, out P25KeyMaterialSource source))
            return "Key available";

        return source == P25KeyMaterialSource.LocalFile
            ? "Available · local file"
            : "Available · FNE/KMM";
    }
}
