using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DvmConsole.Desktop;

/// <summary>
/// Replaces a complete projection with one reset notification. This keeps a
/// stable collection identity for Avalonia bindings without generating tens of
/// thousands of per-row notifications during catalog reconciliation.
/// </summary>
internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] replacement = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (T value in replacement)
            Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}
