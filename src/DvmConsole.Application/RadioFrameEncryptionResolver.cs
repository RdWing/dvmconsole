using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

public readonly record struct RadioFrameEncryption(
    bool IsSecure,
    byte AlgorithmId,
    ushort KeyId);

/// <summary>
/// Extracts receive encryption metadata from a protocol-neutral media frame.
/// Key material is deliberately outside this descriptor.
/// </summary>
public static class RadioFrameEncryptionResolver
{
    public static RadioFrameEncryption? TryResolve(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol == RadioMediaProtocol.P25 &&
            P25DfsiFrameCodec.TryExtractEncryptionMetadata(
                traffic,
                out P25DfsiFrameCodec.P25EncryptionMetadata p25Metadata))
        {
            return new RadioFrameEncryption(
                p25Metadata.AlgorithmId != P25EncryptionAlgorithms.Unencrypted,
                p25Metadata.AlgorithmId,
                p25Metadata.KeyId);
        }

        if (traffic.Protocol == RadioMediaProtocol.Dmr &&
            IsDmrPrivacyHeader(traffic) &&
            DmrVoicePacketCodec.TryExtractEncryptionMetadata(
                traffic.Payload,
                out DmrVoicePacketCodec.DmrEncryptionMetadata dmrMetadata))
        {
            return new RadioFrameEncryption(
                dmrMetadata.AlgorithmId != 0,
                dmrMetadata.AlgorithmId,
                dmrMetadata.KeyId);
        }

        if (traffic.Protocol == RadioMediaProtocol.Nxdn &&
            NxdnVoicePacketCodec.TryExtractCallMetadata(
                traffic.Payload,
                out NxdnVoicePacketCodec.CallMetadata nxdnMetadata) &&
            nxdnMetadata.MessageType == NxdnVoicePacketCodec.VoiceCallMessageType)
        {
            return new RadioFrameEncryption(
                nxdnMetadata.CipherType != 0,
                nxdnMetadata.CipherType,
                nxdnMetadata.KeyId);
        }

        return null;
    }

    private static bool IsDmrPrivacyHeader(IRadioMediaFrame traffic)
        => traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
           traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase);
}
