using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

internal static class ChannelTransmitDefinitionFactory
{
    public static ChannelRuntimeDefinition Create(TransmitChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.Definition.IsEncrypted || channel.TransmitEncrypted)
            return channel.Definition;

        return new ChannelRuntimeDefinition(
            channel.Definition.Name,
            channel.Definition.SystemName,
            channel.Definition.Mode,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            channel.Definition.RxOnly,
            encryptionAlgorithm: "none",
            encryptionKeyId: null,
            selectableEncryption: false);
    }

    public static P25TxEncryptionOptions? CreateEncryptionOptions(
        TransmitChannelDescriptor channel,
        ChannelRuntimeDefinition transmitDefinition,
        IP25KeyResolver? p25KeyResolver)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(transmitDefinition);
        if (!transmitDefinition.IsEncrypted || transmitDefinition.Protocol != ChannelProtocol.P25)
            return null;
        if (p25KeyResolver is null ||
            !P25KeyRing.TryParseAlgorithmId(transmitDefinition.EncryptionAlgorithm, out byte algorithmId) ||
            !P25KeyRing.TryParseKeyId(transmitDefinition.EncryptionKeyId, out ushort keyId) ||
            !p25KeyResolver.TryResolve(
                transmitDefinition.SystemName,
                algorithmId,
                keyId,
                out ReadOnlyMemory<byte> key))
        {
            throw new InvalidOperationException(
                $"P25 transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }

        return P25TxEncryptionOptions.CreateRandom(algorithmId, keyId, key);
    }

    public static DmrPrivacyOptions? CreateDmrPrivacyOptions(
        TransmitChannelDescriptor channel,
        ChannelRuntimeDefinition transmitDefinition,
        IDmrKeyResolver? keyResolver)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(transmitDefinition);
        if (!transmitDefinition.IsEncrypted || transmitDefinition.Protocol != ChannelProtocol.Dmr)
            return null;
        if (keyResolver is null ||
            !DmrKeyRing.TryParseAlgorithmId(transmitDefinition.EncryptionAlgorithm, out byte algorithmId) ||
            !DmrKeyRing.TryParseKeyId(transmitDefinition.EncryptionKeyId, out byte keyId) ||
            !keyResolver.TryResolve(
                transmitDefinition.SystemName,
                algorithmId,
                keyId,
                out ReadOnlyMemory<byte> key))
        {
            throw new InvalidOperationException(
                $"DMR transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }

        return DmrPrivacyOptions.CreateRandom(algorithmId, keyId, key);
    }

    public static NxdnPrivacyOptions? CreateNxdnPrivacyOptions(
        TransmitChannelDescriptor channel,
        ChannelRuntimeDefinition transmitDefinition,
        INxdnKeyResolver? keyResolver)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(transmitDefinition);
        if (!transmitDefinition.IsEncrypted || transmitDefinition.Protocol != ChannelProtocol.Nxdn)
            return null;
        if (keyResolver is null ||
            !NxdnKeyRing.TryParseAlgorithmId(transmitDefinition.EncryptionAlgorithm, out byte algorithmId) ||
            !NxdnKeyRing.TryParseKeyId(transmitDefinition.EncryptionKeyId, out byte keyId) ||
            !keyResolver.TryResolve(
                transmitDefinition.SystemName,
                algorithmId,
                keyId,
                out ReadOnlyMemory<byte> key))
        {
            throw new InvalidOperationException(
                $"NXDN transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }

        return NxdnPrivacyOptions.CreateRandom(algorithmId, keyId, key);
    }
}
