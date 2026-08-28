using DvmConsole.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FilteredDebugLogCollectionTests
{
    [Fact]
    public void NewMatchingTrafficUpdatesIncrementallyWithoutResettingTheView()
    {
        var source = new ObservableCollection<DebugLogEntry>();
        using var filtered = new FilteredDebugLogCollection(source);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)filtered.Entries).CollectionChanged +=
            (_, args) => changes.Add(args.Action);

        source.Add(Entry(DebugLogSeverity.Debug, "packet one"));
        source.Add(Entry(DebugLogSeverity.Info, "call started"));

        DebugLogEntry visible = Assert.Single(filtered.Entries);
        Assert.Equal("call started", visible.Message);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void MatchingInsertionIsAnnouncedBeforeTheVisibleCollectionChanges()
    {
        DebugLogEntry existing = Entry(DebugLogSeverity.Info, "existing");
        DebugLogEntry incoming = Entry(DebugLogSeverity.Info, "incoming");
        var source = new ObservableCollection<DebugLogEntry> { existing };
        using var filtered = new FilteredDebugLogCollection(source);
        var notifications = new List<string>();
        filtered.CollectionChanging += (_, args) =>
        {
            notifications.Add("changing");
            Assert.Equal(NotifyCollectionChangedAction.Add, args.Action);
            Assert.Equal(0, args.NewStartingIndex);
            Assert.Same(existing, Assert.Single(filtered.Entries));
        };
        ((INotifyCollectionChanged)filtered.Entries).CollectionChanged +=
            (_, _) => notifications.Add("changed");

        source.Add(incoming);

        Assert.Equal(["changing", "changed"], notifications);
        Assert.Equal([incoming, existing], filtered.Entries);
    }

    [Fact]
    public void FilterChangesPublishOneResetAndPreserveNewestFirstOrder()
    {
        var source = new ObservableCollection<DebugLogEntry>
        {
            Entry(DebugLogSeverity.Info, "oldest alpha"),
            Entry(DebugLogSeverity.Warning, "middle beta"),
            Entry(DebugLogSeverity.Info, "newest alpha")
        };
        using var filtered = new FilteredDebugLogCollection(source);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)filtered.Entries).CollectionChanged +=
            (_, args) => changes.Add(args.Action);

        filtered.SetFilter("All", "alpha");

        Assert.Equal(
            ["newest alpha", "oldest alpha"],
            filtered.Entries.Select(entry => entry.Message));
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
    }

    [Fact]
    public void EmptySearchDoesNotAllocatePerEntry()
    {
        DebugLogEntry entry = Entry(DebugLogSeverity.Info, "message");
        Assert.True(DebugLogSearch.Matches(entry, string.Empty));
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
            Assert.True(DebugLogSearch.Matches(entry, string.Empty));

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RetentionTurnoverRemovesAndAddsOnlyAffectedRows()
    {
        var buffer = new BoundedDebugLogBuffer(maximumEntries: 2, maximumBytes: 4_096);
        using var filtered = new FilteredDebugLogCollection(buffer.Entries);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)filtered.Entries).CollectionChanged +=
            (_, args) => changes.Add(args.Action);

        buffer.Add(Entry(DebugLogSeverity.Info, "oldest"));
        buffer.Add(Entry(DebugLogSeverity.Info, "middle"));
        changes.Clear();

        buffer.Add(Entry(DebugLogSeverity.Info, "newest"));

        Assert.Equal(
            ["newest", "middle"],
            filtered.Entries.Select(entry => entry.Message));
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add],
            changes);
    }

    [Fact]
    public void VisibleProjectionKeepsOnlyTheNewestMatchingRows()
    {
        var source = new ObservableCollection<DebugLogEntry>
        {
            Entry(DebugLogSeverity.Info, "oldest"),
            Entry(DebugLogSeverity.Info, "middle")
        };
        using var filtered = new FilteredDebugLogCollection(source, maximumVisibleEntries: 2);

        source.Add(Entry(DebugLogSeverity.Info, "newest"));

        Assert.Equal(["newest", "middle"], filtered.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void RetentionDoesNotRemoveAVisibleRowWhenTheEvictedMatchIsOutsideTheProjection()
    {
        DebugLogEntry oldest = Entry(DebugLogSeverity.Info, "oldest");
        DebugLogEntry middle = Entry(DebugLogSeverity.Info, "middle");
        DebugLogEntry newest = Entry(DebugLogSeverity.Info, "newest");
        var source = new ObservableCollection<DebugLogEntry> { oldest, middle, newest };
        using var filtered = new FilteredDebugLogCollection(source, maximumVisibleEntries: 2);

        source.RemoveAt(0);
        source.Add(Entry(DebugLogSeverity.Debug, "not visible"));

        Assert.Equal(["newest", "middle"], filtered.Entries.Select(entry => entry.Message));
    }

    private static DebugLogEntry Entry(DebugLogSeverity severity, string message)
        => new(DateTimeOffset.UnixEpoch, "Test", severity, message);
}
