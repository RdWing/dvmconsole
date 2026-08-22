using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal readonly record struct TrafficEncryptionMetadata(
    bool Secure,
    byte AlgorithmId,
    ushort KeyId);

internal static class TrafficEncryptionMetadataResolver
{
    public static TrafficEncryptionMetadata? TryResolve(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol == FneTrafficProtocol.P25 &&
            P25DfsiFrameCodec.TryExtractEncryptionMetadata(
                traffic,
                out P25DfsiFrameCodec.P25EncryptionMetadata p25Metadata))
        {
            return new TrafficEncryptionMetadata(
                p25Metadata.AlgorithmId != P25EncryptionAlgorithms.Unencrypted,
                p25Metadata.AlgorithmId,
                p25Metadata.KeyId);
        }

        if (traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
            traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase) &&
            DmrVoicePacketCodec.TryExtractEncryptionMetadata(
                traffic.Payload,
                out DmrVoicePacketCodec.DmrEncryptionMetadata dmrMetadata))
        {
            return new TrafficEncryptionMetadata(
                dmrMetadata.AlgorithmId != 0,
                dmrMetadata.AlgorithmId,
                dmrMetadata.KeyId);
        }

        if (traffic.Protocol == FneTrafficProtocol.Nxdn &&
            NxdnVoicePacketCodec.TryExtractCallMetadata(
                traffic.Payload,
                out NxdnVoicePacketCodec.CallMetadata nxdnMetadata) &&
            nxdnMetadata.MessageType == NxdnVoicePacketCodec.VoiceCallMessageType)
        {
            return new TrafficEncryptionMetadata(
                nxdnMetadata.CipherType != 0,
                nxdnMetadata.CipherType,
                nxdnMetadata.KeyId);
        }

        return null;
    }
}
