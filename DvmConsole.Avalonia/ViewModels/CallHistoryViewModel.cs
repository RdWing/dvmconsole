// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.ObjectModel;
using DvmConsole.Avalonia.Services;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// View-model slice for the CALL HISTORY panel: presents the
    /// store's newest-first entries as an observable row collection.
    /// The shell calls <see cref="Refresh"/> — marshalled onto the UI
    /// thread via the store's <see cref="CallHistoryStore.Changed"/>
    /// event — so this view-model performs no store subscription and no
    /// marshaling itself. Rows are immutable <see cref="CallHistoryEntry"/>
    /// instances, so there is no per-row property-change plumbing.
    /// </summary>
    public sealed class CallHistoryViewModel
    {
        /// <summary>
        /// Creates the call-history slice over the given store.
        /// </summary>
        /// <param name="store">The backing call-history store.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="store"/> is null.
        /// </exception>
        public CallHistoryViewModel(CallHistoryStore store)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>The backing store this panel presents.</summary>
        public CallHistoryStore Store { get; }

        /// <summary>
        /// The newest-first history rows, in store order. Empty until
        /// the first <see cref="Refresh"/>.
        /// </summary>
        public ObservableCollection<CallHistoryEntry> Rows { get; } = new();

        /// <summary>
        /// Wholesale resync of <see cref="Rows"/> from
        /// <see cref="CallHistoryStore.Entries"/>: clears and re-adds
        /// every entry in store order. UI-thread only (the shell posts
        /// via the dispatcher).
        /// </summary>
        public void Refresh()
        {
            Rows.Clear();

            foreach (var entry in Store.Entries)
            {
                Rows.Add(entry);
            }
        }
    }
}
