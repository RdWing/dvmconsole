// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

// Keeps settings-page preset filtering local to the presentation model. The
// persisted source collection remains authoritative and in its original order.
internal sealed class FilteredPresetCollection<T>
{
    internal const int FilterVisibilityThreshold = 6;

    private readonly ObservableCollection<T> source;
    private readonly ObservableCollection<T> filtered = [];
    private readonly Func<T, string> searchableText;
    private string filterText = string.Empty;

    public FilteredPresetCollection(
        ObservableCollection<T> source,
        Func<T, string> searchableText)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.searchableText = searchableText ?? throw new ArgumentNullException(nameof(searchableText));
        Items = new ReadOnlyObservableCollection<T>(filtered);
        source.CollectionChanged += HandleSourceChanged;
        Refresh();
    }

    public event EventHandler? StateChanged;

    public ReadOnlyObservableCollection<T> Items { get; }
    public bool IsFilterVisible => source.Count >= FilterVisibilityThreshold || !string.IsNullOrWhiteSpace(filterText);

    public string FilterText
    {
        get => filterText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(filterText, next, StringComparison.Ordinal))
                return;
            filterText = next;
            Refresh();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Refresh();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        string query = filterText.Trim();
        T[] desired = source.Where(item => query.Length == 0 ||
            searchableText(item).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var retained = desired.ToHashSet();
        for (int index = filtered.Count - 1; index >= 0; index--)
            if (!retained.Contains(filtered[index]))
                filtered.RemoveAt(index);
        var existingItems = filtered.ToHashSet();
        for (int index = 0; index < desired.Length; index++)
        {
            if (index < filtered.Count && EqualityComparer<T>.Default.Equals(filtered[index], desired[index]))
                continue;
            int existing = -1;
            for (int candidate = index + 1; existingItems.Contains(desired[index]) && candidate < filtered.Count; candidate++)
                if (EqualityComparer<T>.Default.Equals(filtered[candidate], desired[index]))
                {
                    existing = candidate;
                    break;
                }
            if (existing >= 0)
                filtered.Move(existing, index);
            else
            {
                filtered.Insert(index, desired[index]);
                existingItems.Add(desired[index]);
            }
        }
        while (filtered.Count > desired.Length)
            filtered.RemoveAt(filtered.Count - 1);
    }
}
