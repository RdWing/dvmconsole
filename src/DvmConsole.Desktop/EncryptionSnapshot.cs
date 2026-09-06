// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Application;

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
        return RadioFrameEncryptionResolver.TryResolve(traffic) is { } encryption
            ? EncryptionSnapshot.FromProtocol(
                encryption.IsSecure,
                encryption.AlgorithmId,
                encryption.KeyId)
            : null;
    }
}
