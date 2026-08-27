using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DvmConsole.Desktop;

internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> values)
        => InsertRange(Count, values);

    public void InsertRange(int index, IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (index < 0 || index > Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (values.Count == 0)
            return;

        CheckReentrancy();
        if (Items is List<T> list)
        {
            list.InsertRange(index, values);
        }
        else
        {
            for (int valueIndex = 0; valueIndex < values.Count; valueIndex++)
                Items.Insert(index + valueIndex, values[valueIndex]);
        }

        IList changedItems = values as IList ?? values.ToArray();
        PublishChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            changedItems,
            index));
    }

    public void RemoveRange(int index, int count)
    {
        if (index < 0 || count < 0 || index > Count - count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (count == 0)
            return;

        CheckReentrancy();
        var removed = new T[count];
        for (int removedIndex = 0; removedIndex < count; removedIndex++)
            removed[removedIndex] = Items[index + removedIndex];

        if (Items is List<T> list)
            list.RemoveRange(index, count);
        else
        {
            for (int removedIndex = 0; removedIndex < count; removedIndex++)
                Items.RemoveAt(index);
        }

        PublishChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove,
            removed,
            index));
    }

    public void ReplaceAll(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        CheckReentrancy();
        Items.Clear();
        foreach (T value in values)
            Items.Add(value);
        PublishChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }

    private void PublishChange(NotifyCollectionChangedEventArgs change)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
    }
}
