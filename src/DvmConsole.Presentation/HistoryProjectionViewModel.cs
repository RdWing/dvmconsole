// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;
using System.Collections.ObjectModel;

namespace DvmConsole.Presentation;

public abstract class HistoryProjectionViewModel<T> : CallHistoryFilterViewModel, ICallHistoryViewModel
    where T : class, IHistoryCatalogFilterItem
{
    private readonly ObservableCollection<T> filtered = [];
    protected IReadOnlyList<T> Entries { get; private set; } = [];
    protected HistoryProjectionViewModel() => FilteredCallHistory = new(filtered);
    public ReadOnlyObservableCollection<T> FilteredCallHistory { get; }
    IEnumerable ICallHistoryViewModel.FilteredCallHistory => FilteredCallHistory;

    protected void SetEntries(IReadOnlyList<T> entries)
    {
        Entries = entries;
        RefreshFilteredCallHistory();
    }

    public override void RefreshFilteredCallHistory()
    {
        var filter = CreateHistoryFilter();
        HistoryViewSynchronizer.Synchronize(filtered, filter.IsUnfiltered ? Entries : Entries.Where(item => filter.Matches(item)));
    }
}
