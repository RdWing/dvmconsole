using DvmConsole.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DvmConsole.Desktop;

// Maintains the operator-visible log incrementally. New traffic therefore
// updates only the affected row instead of rebuilding and re-rendering the
// entire retained session log for every packet.
internal sealed class FilteredDebugLogCollection : IDisposable
{
    internal const int DefaultMaximumVisibleEntries = 5_000;

    private readonly ObservableCollection<DebugLogEntry> source;
    private readonly RangeObservableCollection<DebugLogEntry> filtered = [];
    private readonly int maximumVisibleEntries;
    private string severity = "Info";
    private DebugLogSeverity selectedSeverity = DebugLogSeverity.Info;
    private bool includeAllSeverities;
    private bool hasValidSeverity = true;
    private string searchText = string.Empty;

    public FilteredDebugLogCollection(
        ObservableCollection<DebugLogEntry> source,
        int maximumVisibleEntries = DefaultMaximumVisibleEntries)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (maximumVisibleEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumVisibleEntries));
        this.maximumVisibleEntries = maximumVisibleEntries;
        Entries = new ReadOnlyObservableCollection<DebugLogEntry>(filtered);
        source.CollectionChanged += HandleSourceCollectionChanged;
        Rebuild();
    }

    public ReadOnlyObservableCollection<DebugLogEntry> Entries { get; }

    // Raised before Entries changes so virtualized views can preserve their
    // current viewport before item containers are recycled.
    public event NotifyCollectionChangedEventHandler? CollectionChanging;

    public void SetFilter(string severity, string? searchText)
    {
        string normalizedSeverity = string.IsNullOrWhiteSpace(severity) ? "All" : severity;
        string normalizedSearchText = searchText ?? string.Empty;
        if (this.severity.Equals(normalizedSeverity, StringComparison.Ordinal) &&
            this.searchText.Equals(normalizedSearchText, StringComparison.Ordinal))
        {
            return;
        }

        this.severity = normalizedSeverity;
        includeAllSeverities = normalizedSeverity.Equals("All", StringComparison.OrdinalIgnoreCase);
        hasValidSeverity = includeAllSeverities || Enum.TryParse(
            normalizedSeverity,
            ignoreCase: true,
            out selectedSeverity);
        this.searchText = normalizedSearchText;
        Rebuild();
    }

    public void Dispose()
        => source.CollectionChanged -= HandleSourceCollectionChanged;

    private void HandleSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems is not null &&
            e.NewStartingIndex >= 0)
        {
            DebugLogEntry[] matching = e.NewItems
                .OfType<DebugLogEntry>()
                .Where(Matches)
                .Reverse()
                .ToArray();
            if (matching.Length > 0)
            {
                CollectionChanging?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Add,
                        matching,
                        0));
                filtered.InsertRange(0, matching);
                TrimProjection();
            }
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove &&
            e.OldItems is not null &&
            e.OldStartingIndex == 0)
        {
            var removedEntries = e.OldItems
                .OfType<DebugLogEntry>()
                .ToHashSet(ReferenceEqualityComparer.Instance);
            int visibleRemovalCount = filtered.Count(removedEntries.Contains);
            if (visibleRemovalCount > 0)
            {
                // Retention evicts the oldest source rows. Once the projection
                // is capped, some or all of those rows may already be hidden;
                // only remove the evicted instances that are actually visible.
                int filteredIndex = filtered.Count - visibleRemovalCount;
                DebugLogEntry[] removed = filtered
                    .Skip(filteredIndex)
                    .ToArray();
                CollectionChanging?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Remove,
                        removed,
                        filteredIndex));
                filtered.RemoveRange(filteredIndex, visibleRemovalCount);
            }
            return;
        }

        Rebuild();
    }

    private bool Matches(DebugLogEntry entry)
        => hasValidSeverity &&
           (includeAllSeverities || entry.Severity == selectedSeverity) &&
           DebugLogSearch.Matches(entry, searchText);

    private void Rebuild()
    {
        CollectionChanging?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        filtered.ReplaceAll(source.Reverse().Where(Matches).Take(maximumVisibleEntries));
    }

    private void TrimProjection()
    {
        int excess = filtered.Count - maximumVisibleEntries;
        if (excess <= 0)
            return;

        int removalIndex = maximumVisibleEntries;
        DebugLogEntry[] removed = filtered.Skip(removalIndex).ToArray();
        CollectionChanging?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                removed,
                removalIndex));
        filtered.RemoveRange(removalIndex, excess);
    }
}
