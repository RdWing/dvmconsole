// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.FneIntegration;

public sealed class FneReceiveFrameNormalization : IRadioReceiveFrameNormalizer
{
    public static FneReceiveFrameNormalization Instance { get; } = new();
    public IRadioMediaFrame? Normalize(IRadioMediaFrame traffic)
        => traffic is FneTrafficFrame fne ? NormalizeP25CallIdentity(fne) : null;

    private static FneTrafficFrame NormalizeP25CallIdentity(FneTrafficFrame traffic)
    {
        if (!P25DfsiFrameCodec.TryExtractCallIdentifiers(
                traffic,
                out uint sourceId,
                out uint destinationId) ||
            (sourceId == traffic.SourceId && destinationId == traffic.DestinationId))
        {
            return traffic;
        }

        var normalized = new FneTrafficFrame(
            traffic.Protocol,
            traffic.PeerId,
            sourceId,
            destinationId,
            traffic.Slot,
            traffic.CallType,
            traffic.FrameType,
            traffic.Subtype,
            traffic.PacketSequence,
            traffic.StreamId,
            traffic.Payload,
            traffic.FneBoundaryTimestamp,
            traffic.TransportIngressTimestamp);
        P25DfsiFrameCodec.ShareParsedVoiceLdu(traffic, normalized);
        return normalized;
    }

}
