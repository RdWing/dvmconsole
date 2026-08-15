using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using fnecore.P25;

namespace DvmConsole.Desktop;

internal static class ChannelTransmitDefinitionFactory
{
    public static ChannelRuntimeDefinition Create(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.Definition.IsEncrypted || channel.IsTransmitEncrypted)
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
        ChannelViewModel channel,
        ChannelRuntimeDefinition transmitDefinition,
        IP25KeyResolver? p25KeyResolver)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(transmitDefinition);
        if (!transmitDefinition.IsEncrypted)
            return null;
        if (transmitDefinition.Mode != "p25" || p25KeyResolver is null ||
            !P25KeyRing.TryParseAlgorithmId(transmitDefinition.EncryptionAlgorithm, out byte algorithmId) ||
            !P25KeyRing.TryParseKeyId(transmitDefinition.EncryptionKeyId, out ushort keyId) ||
            !p25KeyResolver.TryResolve(algorithmId, keyId, out ReadOnlyMemory<byte> key))
        {
            throw new InvalidOperationException(
                $"P25 transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }

        return P25TxEncryptionOptions.CreateRandom(algorithmId, keyId, key);
    }
}
