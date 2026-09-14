// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

/// <summary>Session-owned recording exclusions; listening and history remain unaffected.</summary>
public sealed class RecordingSubscriberPolicy
{
    private ImmutableSortedSet<uint> ignored = ImmutableSortedSet<uint>.Empty;
    public IReadOnlyCollection<uint> IgnoredSubscribers => Volatile.Read(ref ignored);
    public bool Allows(uint subscriberId) => !Volatile.Read(ref ignored).Contains(subscriberId);

    public void Replace(IEnumerable<uint> subscriberIds)
    {
        ArgumentNullException.ThrowIfNull(subscriberIds);
        Volatile.Write(ref ignored, subscriberIds.Where(id => id != 0).ToImmutableSortedSet());
    }
}
