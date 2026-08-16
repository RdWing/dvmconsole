using System.ComponentModel;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ViewModelPropertySubscriptionTests
{
    [Fact]
    public void RebindDetachesTheOldModelAndOnlyObservesTheReplacement()
    {
        var original = new ObservableModel();
        var replacement = new ObservableModel();
        var observed = new List<string>();
        using var subscription = new ViewModelPropertySubscription<ObservableModel>(
            original,
            (_, args) => observed.Add(args.PropertyName!));

        subscription.Rebind(replacement);
        original.Raise("old");
        replacement.Raise("new");

        Assert.Equal(["new"], observed);
    }

    [Fact]
    public void DisposeDetachesTheCurrentModel()
    {
        var model = new ObservableModel();
        int calls = 0;
        var subscription = new ViewModelPropertySubscription<ObservableModel>(model, (_, _) => calls++);

        subscription.Dispose();
        model.Raise("after-dispose");

        Assert.Equal(0, calls);
    }

    private sealed class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
