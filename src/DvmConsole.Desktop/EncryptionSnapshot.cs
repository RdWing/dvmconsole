using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal enum EncryptionEvidence
{
    Unknown,
    Inferred,
    Configured,
    Protocol
}

// The canonical in-memory representation of a call's encryption state. The
// evidence level prevents a protocol inference from overwriting explicit wire
// metadata while still allowing late protocol headers to correct that inference.
internal readonly record struct EncryptionSnapshot
{
    private EncryptionSnapshot(
        CallRecordingEncryptionState state,
        byte? algorithmId,
        ushort? keyId,
        EncryptionEvidence evidence)
    {
        State = state;
        AlgorithmId = state == CallRecordingEncryptionState.Secure ? algorithmId : null;
        KeyId = state == CallRecordingEncryptionState.Secure ? keyId : null;
        Evidence = state == CallRecordingEncryptionState.Unknown
            ? EncryptionEvidence.Unknown
            : evidence;
    }

    public CallRecordingEncryptionState State { get; }
    public byte? AlgorithmId { get; }
    public ushort? KeyId { get; }
    public EncryptionEvidence Evidence { get; }
    public bool IsKnown => State != CallRecordingEncryptionState.Unknown;
    public bool IsSecure => State == CallRecordingEncryptionState.Secure;

    public static EncryptionSnapshot Unknown => default;
    public static EncryptionSnapshot InferredClear { get; } = new(
        CallRecordingEncryptionState.Clear,
        null,
        null,
        EncryptionEvidence.Inferred);

    public static EncryptionSnapshot FromProtocol(bool secure, byte algorithmId, ushort keyId)
        => new(
            secure ? CallRecordingEncryptionState.Secure : CallRecordingEncryptionState.Clear,
            algorithmId,
            keyId,
            EncryptionEvidence.Protocol);

    public static EncryptionSnapshot FromConfiguration(
        bool secure,
        byte? algorithmId = null,
        ushort? keyId = null)
        => new(
            secure ? CallRecordingEncryptionState.Secure : CallRecordingEncryptionState.Clear,
            algorithmId,
            keyId,
            EncryptionEvidence.Configured);

    public static EncryptionSnapshot FromStored(
        CallRecordingEncryptionState state,
        byte? algorithmId = null,
        ushort? keyId = null)
        => new(state, algorithmId, keyId, EncryptionEvidence.Configured);

    public bool HasSameMetadata(EncryptionSnapshot other)
        => State == other.State &&
           AlgorithmId == other.AlgorithmId &&
           KeyId == other.KeyId;
}

internal static class EncryptionSnapshotResolver
{
    public static EncryptionSnapshot? TryResolve(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol == RadioMediaProtocol.P25 &&
            P25DfsiFrameCodec.TryExtractEncryptionMetadata(
                traffic,
                out P25DfsiFrameCodec.P25EncryptionMetadata p25Metadata))
        {
            return EncryptionSnapshot.FromProtocol(
                p25Metadata.AlgorithmId != P25EncryptionAlgorithms.Unencrypted,
                p25Metadata.AlgorithmId,
                p25Metadata.KeyId);
        }

        if (traffic.Protocol == RadioMediaProtocol.Dmr &&
            traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
            traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase) &&
            DmrVoicePacketCodec.TryExtractEncryptionMetadata(
                traffic.Payload,
                out DmrVoicePacketCodec.DmrEncryptionMetadata dmrMetadata))
        {
            return EncryptionSnapshot.FromProtocol(
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
            return EncryptionSnapshot.FromProtocol(
                nxdnMetadata.CipherType != 0,
                nxdnMetadata.CipherType,
                nxdnMetadata.KeyId);
        }

        return null;
    }
}
