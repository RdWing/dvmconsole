using System.Collections.ObjectModel;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CollectionChangedSubscriptionTests
{
    [Fact]
    public void RebindDetachesThePreviousCollectionAndObservesTheReplacement()
    {
        var initial = new ObservableCollection<int>();
        var replacement = new ObservableCollection<int>();
        int calls = 0;
        using var subscription = new CollectionChangedSubscription(
            initial,
            (_, _) => calls++);

        initial.Add(1);
        subscription.Rebind(replacement);
        initial.Add(2);
        replacement.Add(3);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void DisposeDetachesTheCurrentCollection()
    {
        var collection = new ObservableCollection<int>();
        int calls = 0;
        var subscription = new CollectionChangedSubscription(
            collection,
            (_, _) => calls++);

        subscription.Dispose();
        collection.Add(1);

        Assert.Equal(0, calls);
    }
}
