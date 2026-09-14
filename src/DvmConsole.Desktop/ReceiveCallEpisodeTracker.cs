// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Compatibility at the FNE boundary; episode ownership and reduction live in Application.
internal static class ReceiveCallEpisodePolicy
{
    public static TimeSpan GetContinuationWindow(FneTrafficProtocol protocol)
        => RadioCallEpisodePolicy.GetContinuationWindow(FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol));
}

internal static class ReceiveCallEpisodeFneAdapter
{
    public static bool TryGet(this ReceiveCallEpisodeTracker tracker, string systemName,
        FneTrafficProtocol protocol, uint physicalStreamId, out ReceiveCallEpisodeSnapshot snapshot)
        => tracker.TryGet(systemName, FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol), physicalStreamId, out snapshot);

    public static void ObservePhysicalEnd(this ReceiveCallEpisodeTracker tracker, string systemName,
        FneTrafficProtocol protocol, uint physicalStreamId, DateTimeOffset endedAt, ReceivePhysicalEndReason reason)
        => tracker.ObservePhysicalEnd(systemName, FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol), physicalStreamId, endedAt, reason);
}
