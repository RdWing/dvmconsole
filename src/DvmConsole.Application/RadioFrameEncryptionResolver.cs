// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using System.Runtime.CompilerServices;

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
    private static readonly ConditionalWeakTable<IRadioMediaFrame, CacheEntry> Cache = new();

    public static RadioFrameEncryption? TryResolve(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (!CanCarryEncryptionMetadata(traffic))
            return null;

        return GetEntry(traffic).Encryption;
    }

    public static bool TryResolveNxdnCallMetadata(
        IRadioMediaFrame traffic,
        out NxdnVoicePacketCodec.CallMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (!CanCarryNxdnCallMetadata(traffic))
        {
            metadata = default;
            return false;
        }

        if (GetEntry(traffic).NxdnCallMetadata is { } resolved)
        {
            metadata = resolved;
            return true;
        }

        metadata = default;
        return false;
    }

    private static CacheEntry GetEntry(IRadioMediaFrame traffic)
        => Cache.GetValue(traffic, static frame => new CacheEntry(frame));

    private static RadioFrameEncryption? ResolveEncryption(
        IRadioMediaFrame traffic,
        NxdnVoicePacketCodec.CallMetadata? nxdnMetadata)
    {
        if (traffic.Protocol == RadioMediaProtocol.P25 &&
            P25DfsiFrameCodec.TryExtractEncryptionIdentity(
                traffic,
                out byte p25AlgorithmId,
                out ushort p25KeyId))
        {
            return new RadioFrameEncryption(
                p25AlgorithmId != P25EncryptionAlgorithms.Unencrypted,
                p25AlgorithmId,
                p25KeyId);
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

        if (nxdnMetadata is { MessageType: NxdnVoicePacketCodec.VoiceCallMessageType } metadata)
        {
            return new RadioFrameEncryption(
                metadata.CipherType != 0,
                metadata.CipherType,
                metadata.KeyId);
        }

        return null;
    }

    private static bool IsDmrPrivacyHeader(IRadioMediaFrame traffic)
        => traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
           traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase);

    // Most receive frames are ordinary voice continuations. Rejecting the
    // message classes that cannot carry privacy facts avoids creating a weak
    // table entry for every 20 ms DMR/analog callback while preserving a
    // single structural parse for P25 LDUs and NXDN control-bearing frames.
    private static bool CanCarryEncryptionMetadata(IRadioMediaFrame traffic)
        => traffic.Protocol switch
        {
            RadioMediaProtocol.Dmr => IsDmrPrivacyHeader(traffic),
            RadioMediaProtocol.P25 =>
                traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) &&
                (traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase) ||
                 traffic.Subtype.Equals("LDU2", StringComparison.OrdinalIgnoreCase)),
            RadioMediaProtocol.Nxdn => CanCarryNxdnCallMetadata(traffic),
            _ => false
        };

    private static bool CanCarryNxdnCallMetadata(IRadioMediaFrame traffic)
        => traffic.Protocol == RadioMediaProtocol.Nxdn &&
           (traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase));

    private sealed class CacheEntry
    {
        public CacheEntry(IRadioMediaFrame traffic)
        {
            if (traffic.Protocol == RadioMediaProtocol.Nxdn &&
                NxdnVoicePacketCodec.TryExtractCallMetadata(
                    traffic.Payload,
                    out NxdnVoicePacketCodec.CallMetadata metadata))
            {
                NxdnCallMetadata = metadata;
            }
            Encryption = ResolveEncryption(traffic, NxdnCallMetadata);
        }

        public RadioFrameEncryption? Encryption { get; }
        public NxdnVoicePacketCodec.CallMetadata? NxdnCallMetadata { get; }
    }
}
