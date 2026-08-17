using DvmConsole.FneClient;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using fnecore.P25;

namespace DvmConsole.Desktop;

internal static class EncryptionPresentation
{
    public static string StatusText(bool secure, FneTrafficProtocol protocol, byte? algorithmId)
    {
        if (!secure)
            return "Clear";

        string algorithm = AlgorithmAbbreviation(protocol, algorithmId);
        return string.IsNullOrEmpty(algorithm) ? "Secure" : $"Secure · {algorithm}";
    }

    public static string AlgorithmAbbreviation(FneTrafficProtocol protocol, byte? algorithmId)
    {
        if (algorithmId is not byte value)
            return string.Empty;

        return protocol switch
        {
            FneTrafficProtocol.P25 => value switch
            {
                P25Defines.P25_ALGO_AES => "AES",
                P25Defines.P25_ALGO_DES => "DES",
                P25Defines.P25_ALGO_ARC4 => "RC4",
                _ => string.Empty
            },
            FneTrafficProtocol.Dmr => value switch
            {
                DmrPrivacyAlgorithms.Aes256 => "AES",
                DmrPrivacyAlgorithms.DesOfb => "DES",
                DmrPrivacyAlgorithms.Arc4 => "RC4",
                _ => string.Empty
            },
            FneTrafficProtocol.Nxdn => value switch
            {
                NxdnPrivacyAlgorithms.Aes256 => "AES",
                NxdnPrivacyAlgorithms.Des => "DES",
                NxdnPrivacyAlgorithms.Ehr => "EHR",
                _ => string.Empty
            },
            _ => string.Empty
        };
    }

    public static bool TryParseConfiguredAlgorithm(
        ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        algorithmId = 0;
        keyId = 0;
        return definition.Mode switch
        {
            "p25" => P25KeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) &&
                     P25KeyRing.TryParseKeyId(definition.EncryptionKeyId, out keyId),
            "dmr" => TryParseDmr(definition, out algorithmId, out keyId),
            "nxdn" => TryParseNxdn(definition, out algorithmId, out keyId),
            _ => false
        };
    }

    private static bool TryParseDmr(
        ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        keyId = 0;
        if (!DmrKeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) ||
            !DmrKeyRing.TryParseKeyId(definition.EncryptionKeyId, out byte parsedKeyId))
        {
            return false;
        }
        keyId = parsedKeyId;
        return true;
    }

    private static bool TryParseNxdn(
        ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        keyId = 0;
        if (!NxdnKeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) ||
            !NxdnKeyRing.TryParseKeyId(definition.EncryptionKeyId, out byte parsedKeyId))
        {
            return false;
        }
        keyId = parsedKeyId;
        return true;
    }
}
