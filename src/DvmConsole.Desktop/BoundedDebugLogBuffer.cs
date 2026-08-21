using DvmConsole.Core.Diagnostics;
using System.Collections.ObjectModel;

namespace DvmConsole.Desktop;

// Owns the in-memory lifetime of one application session's diagnostic log.
// The UI and export paths observe the same bounded collection.
internal sealed class BoundedDebugLogBuffer
{
    internal const int DefaultMaximumEntries = 5_000;
    internal const long DefaultMaximumBytes = 4L * 1024 * 1024;
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

    public ObservableCollection<DebugLogEntry> Entries { get; } = [];

    public string RetentionText
    {
        get
        {
            string discarded = discardedEntries > 0
                ? $" · oldest discarded {discardedEntries:N0}"
                : string.Empty;
            return $"Session log · {Entries.Count:N0} entries · {FormatBytes(retainedBytes)} in memory" +
                $" · limit {maximumEntries:N0} entries / {FormatBytes(maximumBytes)}{discarded}";
        }
    }

    public void Add(DebugLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DebugLogEntry retained = entry.Message.Length <= MaximumMessageCharacters
            ? entry
            : entry with
            {
                Message = entry.Message[..MaximumMessageCharacters] +
                    "… [message truncated to protect session log memory]"
            };
        long entryBytes = EstimateBytes(retained);

        while (Entries.Count > 0 &&
               (Entries.Count >= maximumEntries || retainedBytes > maximumBytes - entryBytes))
        {
            RemoveOldest();
        }

        if (entryBytes > maximumBytes)
        {
            discardedEntries = SaturatingIncrement(discardedEntries);
            return;
        }

        Entries.Insert(0, retained);
        retainedBytes += entryBytes;
    }

    private void RemoveOldest()
    {
        DebugLogEntry oldest = Entries[^1];
        retainedBytes = Math.Max(0, retainedBytes - EstimateBytes(oldest));
        Entries.RemoveAt(Entries.Count - 1);
        discardedEntries = SaturatingIncrement(discardedEntries);
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
