// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DvmConsole.Presentation;

public static class HistoryViewSynchronizer
{
    public static void Synchronize<T>(
        ObservableCollection<T> target,
        IEnumerable<T> desiredEntries,
        Action<NotifyCollectionChangedEventArgs>? collectionChanging = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desiredEntries);
        T[] desired = desiredEntries as T[] ?? desiredEntries.ToArray();
        lock (target)
        {
            if (HasSameEntries(target, desired))
                return;

            var desiredSet = new HashSet<T>(
                desired,
                ReferenceEqualityComparer.Instance);
            for (int index = target.Count - 1; index >= 0; index--)
            {
                if (!desiredSet.Contains(target[index]))
                {
                    collectionChanging?.Invoke(new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Remove,
                        target[index],
                        index));
                    target.RemoveAt(index);
                }
            }

            var present = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
            for (int index = 0; index < desired.Length; index++)
            {
                if (index < target.Count && ReferenceEquals(target[index], desired[index]))
                    continue;
                int existingIndex = -1;
                if (present.Contains(desired[index]))
                {
                    for (int candidate = index + 1; candidate < target.Count; candidate++)
                        if (ReferenceEquals(target[candidate], desired[index]))
                        {
                            existingIndex = candidate;
                            break;
                        }
                }
                if (existingIndex >= 0)
                {
                    collectionChanging?.Invoke(new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Move,
                        desired[index],
                        index,
                        existingIndex));
                    target.Move(existingIndex, index);
                }
                else
                {
                    collectionChanging?.Invoke(new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction.Add,
                        desired[index],
                        index));
                    target.Insert(index, desired[index]);
                    present.Add(desired[index]);
                }
            }
        }
    }

    private static bool HasSameEntries<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired) where T : class
    {
        if (target.Count != desired.Count)
            return false;
        for (int index = 0; index < target.Count; index++)
        {
            if (!ReferenceEquals(target[index], desired[index]))
                return false;
        }
        return true;
    }
}
