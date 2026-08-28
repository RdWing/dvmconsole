using DvmConsole.FneClient;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal static class EncryptionPresentation
{
    public static FneTrafficProtocol ParseProtocol(string? protocol)
        => protocol?.Trim().ToUpperInvariant() switch
        {
            "P25" => FneTrafficProtocol.P25,
            "NXDN" => FneTrafficProtocol.Nxdn,
            "ANALOG" => FneTrafficProtocol.Analog,
            _ => FneTrafficProtocol.Dmr
        };

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
                P25EncryptionAlgorithms.Aes => "AES",
                P25EncryptionAlgorithms.Des => "DES",
                P25EncryptionAlgorithms.Arc4 => "RC4",
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

    public static string AlgorithmDisplayName(FneTrafficProtocol protocol, byte? algorithmId)
        => protocol switch
        {
            FneTrafficProtocol.Dmr when algorithmId == DmrPrivacyAlgorithms.Aes256 => "AES-256",
            FneTrafficProtocol.Dmr when algorithmId == DmrPrivacyAlgorithms.DesOfb => "DES-OFB",
            FneTrafficProtocol.Nxdn when algorithmId == NxdnPrivacyAlgorithms.Aes256 => "AES-256",
            _ => AlgorithmAbbreviation(protocol, algorithmId)
        };

    public static bool TryParseAlgorithmAbbreviation(
        FneTrafficProtocol protocol,
        string? abbreviation,
        out byte algorithmId)
    {
        algorithmId = 0;
        if (string.IsNullOrWhiteSpace(abbreviation))
            return false;

        string normalized = abbreviation.Trim().ToUpperInvariant();
        algorithmId = protocol switch
        {
            FneTrafficProtocol.P25 when normalized == "AES" => P25EncryptionAlgorithms.Aes,
            FneTrafficProtocol.P25 when normalized == "DES" => P25EncryptionAlgorithms.Des,
            FneTrafficProtocol.P25 when normalized == "RC4" => P25EncryptionAlgorithms.Arc4,
            FneTrafficProtocol.Dmr when normalized is "AES" or "AES-256" => DmrPrivacyAlgorithms.Aes256,
            FneTrafficProtocol.Dmr when normalized is "DES" or "DES-OFB" => DmrPrivacyAlgorithms.DesOfb,
            FneTrafficProtocol.Dmr when normalized == "RC4" => DmrPrivacyAlgorithms.Arc4,
            FneTrafficProtocol.Nxdn when normalized is "AES" or "AES-256" => NxdnPrivacyAlgorithms.Aes256,
            FneTrafficProtocol.Nxdn when normalized == "DES" => NxdnPrivacyAlgorithms.Des,
            FneTrafficProtocol.Nxdn when normalized == "EHR" => NxdnPrivacyAlgorithms.Ehr,
            _ => 0
        };
        return algorithmId != 0;
    }

    public static bool TryParseConfiguredAlgorithm(
        ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        algorithmId = 0;
        keyId = 0;
        return definition.Protocol switch
        {
            ChannelProtocol.P25 => P25KeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) &&
                                   P25KeyRing.TryParseKeyId(definition.EncryptionKeyId, out keyId),
            ChannelProtocol.Dmr => TryParseDmr(definition, out algorithmId, out keyId),
            ChannelProtocol.Nxdn => TryParseNxdn(definition, out algorithmId, out keyId),
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
