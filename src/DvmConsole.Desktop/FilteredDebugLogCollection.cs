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
    private readonly ResettableObservableCollection<DebugLogEntry> filtered = [];
    private string severity = "Info";
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
            int sourceIndex = e.NewStartingIndex;
            foreach (DebugLogEntry entry in e.NewItems.OfType<DebugLogEntry>())
            {
                if (Matches(entry))
                {
                    int filteredIndex = CountMatchesBefore(sourceIndex);
                    CollectionChanging?.Invoke(
                        this,
                        new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Add,
                            entry,
                            filteredIndex));
                    filtered.Insert(filteredIndex, entry);
                }
                sourceIndex++;
            }
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (DebugLogEntry entry in e.OldItems.OfType<DebugLogEntry>())
            {
                int index = FindByReference(entry);
                if (index >= 0)
                {
                    CollectionChanging?.Invoke(
                        this,
                        new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Remove,
                            entry,
                            index));
                    filtered.RemoveAt(index);
                }
            }
            return;
        }

        Rebuild();
    }

    private int CountMatchesBefore(int sourceIndex)
    {
        int count = 0;
        for (int index = 0; index < sourceIndex && index < source.Count; index++)
        {
            if (Matches(source[index]))
                count++;
        }
        return count;
    }

    private int FindByReference(DebugLogEntry entry)
    {
        for (int index = 0; index < filtered.Count; index++)
        {
            if (ReferenceEquals(filtered[index], entry))
                return index;
        }
        return -1;
    }

    private bool Matches(DebugLogEntry entry)
        => (severity == "All" ||
            entry.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase)) &&
           DebugLogSearch.Matches(entry, searchText);

    private void Rebuild()
    {
        CollectionChanging?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        filtered.ReplaceAll(source.Where(Matches));
    }

    private sealed class ResettableObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            Items.Clear();
            foreach (T value in values)
                Items.Add(value);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Reset));
        }
    }
}
