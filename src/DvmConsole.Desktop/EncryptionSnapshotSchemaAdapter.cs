using System.Globalization;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Keeps compatibility fields at persistence boundaries. Runtime code consumes
// EncryptionSnapshot so clear, secure, and unknown cannot drift independently.
internal static class EncryptionSnapshotSchemaAdapter
{
    public static EncryptionSnapshot FromDescriptor(RecordingFinalizationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.EncryptionKnown)
            return EncryptionSnapshot.Unknown;
        return EncryptionSnapshot.FromStored(
            descriptor.IsSecure
                ? CallRecordingEncryptionState.Secure
                : CallRecordingEncryptionState.Clear,
            descriptor.EncryptionAlgorithmId,
            descriptor.EncryptionKeyId);
    }

    public static EncryptionSnapshot FromMetadata(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        CallRecordingEncryptionState state = metadata.SchemaVersion >=
            CallRecordingMetadata.CurrentSchemaVersion
                ? metadata.EncryptionState
                : metadata.IsEncrypted
                    ? CallRecordingEncryptionState.Secure
                    : CallRecordingEncryptionState.Clear;
        if (state != CallRecordingEncryptionState.Secure)
            return EncryptionSnapshot.FromStored(state);

        FneTrafficProtocol protocol = EncryptionPresentation.ParseProtocol(metadata.Protocol);
        byte? algorithmId = metadata.EncryptionAlgorithmId;
        if (algorithmId is null &&
            EncryptionPresentation.TryParseAlgorithmAbbreviation(
                protocol,
                metadata.EncryptionAlgorithm,
                out byte parsedAlgorithmId))
        {
            algorithmId = parsedAlgorithmId;
        }

        ushort? keyId = metadata.EncryptionKeyIdValue ?? ParseKeyId(metadata.EncryptionKeyId);
        return EncryptionSnapshot.FromStored(state, algorithmId, keyId);
    }

    public static void ApplyToMetadata(
        CallRecordingMetadata metadata,
        EncryptionSnapshot encryption,
        RadioMediaProtocol protocol)
        => ApplyToMetadata(metadata, encryption, EncryptionPresentation.ToFneProtocol(protocol));

    public static void ApplyToMetadata(
        CallRecordingMetadata metadata,
        EncryptionSnapshot encryption,
        FneTrafficProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.EncryptionState = encryption.State;
        metadata.IsEncrypted = encryption.IsSecure;
        metadata.EncryptionAlgorithmId = encryption.AlgorithmId;
        metadata.EncryptionAlgorithm = encryption.IsSecure
            ? EncryptionPresentation.AlgorithmDisplayName(protocol, encryption.AlgorithmId)
            : string.Empty;
        metadata.EncryptionKeyIdValue = encryption.KeyId;
        metadata.EncryptionKeyId = encryption.KeyId is ushort keyId
            ? $"0x{keyId:X}"
            : null;
    }

    private static ushort? ParseKeyId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ushort.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out ushort keyId)
                ? keyId
                : null;
    }
}
