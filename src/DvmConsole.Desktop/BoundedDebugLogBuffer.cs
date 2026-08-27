using DvmConsole.Core.Diagnostics;
using System.Collections.ObjectModel;

namespace DvmConsole.Desktop;

// Owns the in-memory lifetime of one application session's diagnostic log.
// The UI and export paths observe the same bounded collection.
internal sealed class BoundedDebugLogBuffer
{
    internal const long DefaultMaximumBytes = 100L * 1024 * 1024;
    internal const int DefaultMaximumEntries = 50_000;
    internal const int MaximumMessageCharacters = 16_384;
    private const int EstimatedEntryOverheadBytes = 128;

    private readonly int maximumEntries;
    private readonly long maximumBytes;
    private long retainedBytes;
    private long discardedEntries;

    public BoundedDebugLogBuffer(
        int maximumEntries = DefaultMaximumEntries,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumBytes < EstimatedEntryOverheadBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumEntries = maximumEntries;
        this.maximumBytes = maximumBytes;
    }

    // The retained store is chronological so steady-state ingestion appends.
    // Operator-facing views reverse only their filtered projection.
    public RangeObservableCollection<DebugLogEntry> Entries { get; } = [];

    public string RetentionText
    {
        get
        {
            string discarded = discardedEntries > 0
                ? $" · oldest discarded {discardedEntries:N0}"
                : string.Empty;
            string limit = $"{maximumEntries:N0} entries / {FormatBytes(maximumBytes)}";
            return $"Session log · {Entries.Count:N0} entries · {FormatBytes(retainedBytes)} in memory" +
                $" · limit {limit}{discarded}";
        }
    }

    public void Add(DebugLogEntry entry)
        => AddRange([entry]);

    public void AddRange(IReadOnlyList<DebugLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        var retainedEntries = new List<DebugLogEntry>(entries.Count);
        long addedBytes = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            DebugLogEntry entry = entries[index] ??
                throw new ArgumentException("Log entries cannot contain null.", nameof(entries));
            DebugLogEntry retained = entry.Message.Length <= MaximumMessageCharacters
                ? entry
                : entry with
                {
                    Message = entry.Message[..MaximumMessageCharacters] +
                        "… [message truncated to protect session log memory]"
                };
            long entryBytes = EstimateBytes(retained);
            if (entryBytes > maximumBytes)
            {
                discardedEntries = SaturatingIncrement(discardedEntries);
                continue;
            }

            retainedEntries.Add(retained);
            addedBytes += entryBytes;
        }

        int removeCount = 0;
        long remainingBytes = retainedBytes;
        while (removeCount < Entries.Count &&
               (Entries.Count - removeCount + retainedEntries.Count > maximumEntries ||
                remainingBytes + addedBytes > maximumBytes))
        {
            remainingBytes = Math.Max(
                0,
                remainingBytes - EstimateBytes(Entries[removeCount]));
            discardedEntries = SaturatingIncrement(discardedEntries);
            removeCount++;
        }

        if (removeCount > 0)
            Entries.RemoveRange(0, removeCount);

        int removeIncomingCount = 0;
        while (removeIncomingCount < retainedEntries.Count &&
               (retainedEntries.Count - removeIncomingCount > maximumEntries ||
                remainingBytes + addedBytes > maximumBytes))
        {
            addedBytes -= EstimateBytes(retainedEntries[removeIncomingCount]);
            discardedEntries = SaturatingIncrement(discardedEntries);
            removeIncomingCount++;
        }
        if (removeIncomingCount > 0)
            retainedEntries.RemoveRange(0, removeIncomingCount);

        retainedBytes = remainingBytes + addedBytes;
        Entries.AddRange(retainedEntries);
    }

    private static long EstimateBytes(DebugLogEntry entry)
        => EstimatedEntryOverheadBytes +
           ((long)entry.Source.Length + entry.Message.Length) * sizeof(char);

    private static long SaturatingIncrement(long value)
        => value == long.MaxValue ? long.MaxValue : value + 1;

    private static string FormatBytes(long bytes)
        => bytes < 1024
            ? $"{bytes:N0} B"
            : bytes < 1024 * 1024
                ? $"{bytes / 1024d:0.0} KB"
                : $"{bytes / (1024d * 1024d):0.0} MB";
}
