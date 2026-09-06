// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#nullable enable

namespace fnecore;

internal sealed record FneTalkgroupAnnouncementEntry(
    uint DestinationId,
    byte Slot,
    bool AffiliationRequired,
    bool NonPreferred);

internal sealed record FneTalkgroupAnnouncement(
    bool ContainsActiveTalkgroups,
    IReadOnlyList<FneTalkgroupAnnouncementEntry> Entries);
