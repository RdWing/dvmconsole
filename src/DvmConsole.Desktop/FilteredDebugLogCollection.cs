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
    private readonly ObservableCollection<DebugLogEntry> source;
    private readonly RangeObservableCollection<DebugLogEntry> filtered = [];
    private string severity = "Info";
    private DebugLogSeverity selectedSeverity = DebugLogSeverity.Info;
    private bool includeAllSeverities;
    private bool hasValidSeverity = true;
    private string searchText = string.Empty;

    public FilteredDebugLogCollection(ObservableCollection<DebugLogEntry> source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
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
            }
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove &&
            e.OldItems is not null &&
            e.OldStartingIndex == 0)
        {
            int matchingCount = e.OldItems
                .OfType<DebugLogEntry>()
                .Count(Matches);
            if (matchingCount > 0)
            {
                int filteredIndex = filtered.Count - matchingCount;
                DebugLogEntry[] removed = filtered
                    .Skip(filteredIndex)
                    .ToArray();
                CollectionChanging?.Invoke(
                    this,
                    new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Remove,
                        removed,
                        filteredIndex));
                filtered.RemoveRange(filteredIndex, matchingCount);
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
        filtered.ReplaceAll(source.Reverse().Where(Matches));
    }
}
