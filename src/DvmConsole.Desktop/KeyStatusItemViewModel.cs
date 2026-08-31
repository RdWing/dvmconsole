using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Redacted operator-facing encryption status.  It intentionally exposes
// algorithm and key identifiers but never key material.
public sealed record KeyStatusItemViewModel(
    string SystemName,
    string ChannelName,
    string ModeText,
    string AlgorithmIdText,
    string KeyIdText,
    string StatusText,
    string ConfigurationHint) : IKeyStatusItemViewModel
{
    public bool HasConfigurationHint => !string.IsNullOrEmpty(ConfigurationHint);

    public static KeyStatusItemViewModel From(
        ChannelViewModel channel,
        IP25KeyResolver? keyResolver,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        ChannelProtocol protocol = channel.Definition.Protocol;
        bool isDmr = protocol == ChannelProtocol.Dmr;
        bool isNxdn = protocol == ChannelProtocol.Nxdn;
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
        bool available = protocol switch
        {
            ChannelProtocol.P25 => keyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            ChannelProtocol.Dmr => dmrKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            ChannelProtocol.Nxdn => nxdnKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            _ => false
        };
        string statusText = !channel.Definition.IsEncrypted
            ? "Clear"
            : !ChannelProtocolMediaMapper.RequiresVocoder(protocol)
                ? "Unsupported protocol"
                : available
                    ? protocol == ChannelProtocol.P25
                        ? DescribeAvailableKey(
                            keyResolver!,
                            channel.Definition.SystemName,
                            algorithmId,
                            keyId)
                        : "Available · local file"
                    : "Key unavailable";
        string configurationHint = available
            ? string.Empty
            : DescribeLocalKeyRequirement(protocol, parsedAlgorithm, algorithmId);

        return new KeyStatusItemViewModel(
            channel.Definition.SystemName,
            channel.Name,
            channel.ModeText,
            algorithmText,
            keyIdText,
            statusText,
            configurationHint);
    }

    private static string DescribeLocalKeyRequirement(
        ChannelProtocol protocol,
        bool parsedAlgorithm,
        byte algorithmId)
    {
        if (!parsedAlgorithm || protocol != ChannelProtocol.Dmr)
            return string.Empty;

        int keyBytes;
        try
        {
            keyBytes = DmrPrivacyAlgorithms.KeyBytes(algorithmId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }

        return $"Local entry: protocol: \"dmr\" · algId: 0x{algorithmId:X2} · key: {keyBytes} bytes";
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
