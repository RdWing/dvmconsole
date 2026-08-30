#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only

namespace fnecore;

internal sealed record FneTalkgroupAnnouncementEntry(
    uint DestinationId,
    byte Slot,
    bool AffiliationRequired,
    bool NonPreferred);

internal sealed record FneTalkgroupAnnouncement(
    bool ContainsActiveTalkgroups,
    IReadOnlyList<FneTalkgroupAnnouncementEntry> Entries);
