// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class HistoryViewportTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PrependingPreservesVisibleRecordAndPixelOffset(bool evict, bool virtualized)
    {
        using var fixture = new ViewportFixture(virtualized);
        fixture.Scroll.Offset = new Vector(0, 517);
        fixture.Window.UpdateLayout();
        var before = fixture.FirstVisible();
        var desired = new[] { new Record(73) }.Concat(fixture.Items).Take(evict ? 100 : 101).ToArray();
        HistoryViewSynchronizer.Synchronize(fixture.Items, desired, fixture.Controller.HandleCollectionChangingForTest);
        fixture.Window.UpdateLayout();
        fixture.Window.UpdateLayout();
        Assert.Same(before.Record, fixture.FirstVisible().Record);
        Assert.Equal(before.Y, fixture.FirstVisible().Y, 2);
        Assert.False(fixture.Anchor.HasPendingRestore);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrependingAtTopFollowsNewestRecord(bool virtualized)
    {
        using var fixture = new ViewportFixture(virtualized);
        var newest = new Record(73);
        HistoryViewSynchronizer.Synchronize(fixture.Items, new[] { newest }.Concat(fixture.Items).ToArray(),
            fixture.Controller.HandleCollectionChangingForTest);
        fixture.Window.UpdateLayout();
        Assert.Equal(0, fixture.Scroll.Offset.Y);
        Assert.Same(newest, fixture.FirstVisible().Record);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveArrivalsBeforeOffsetLayoutPreserveOriginalAnchor(bool virtualized)
    {
        using var fixture = new ViewportFixture(virtualized);
        fixture.Scroll.Offset = new Vector(0, 517);
        fixture.Window.UpdateLayout();
        var before = fixture.FirstVisible();
        foreach (double height in new[] { 73d, 89d })
        {
            HistoryViewSynchronizer.Synchronize(fixture.Items, new[] { new Record(height) }.Concat(fixture.Items).Take(100).ToArray(),
                fixture.Controller.HandleCollectionChangingForTest);
            fixture.Window.UpdateLayout();
        }
        fixture.Window.UpdateLayout();
        fixture.Window.UpdateLayout();
        Assert.Same(before.Record, fixture.FirstVisible().Record);
        Assert.Equal(before.Y, fixture.FirstVisible().Y, 2);
    }

    [AvaloniaFact]
    public void BurstWithEvictionRealizesTheOriginalVirtualizedRecord()
    {
        using var fixture = new ViewportFixture(true);
        fixture.Scroll.Offset = new Vector(0, 517);
        fixture.Window.UpdateLayout();
        var before = fixture.FirstVisible();
        var burst = Enumerable.Range(0, 40).Select(index => new Record(37 + index % 4 * 11));
        HistoryViewSynchronizer.Synchronize(fixture.Items, burst.Concat(fixture.Items).Take(100).ToArray(),
            fixture.Controller.HandleCollectionChangingForTest);
        // Collection layout, realization, offset correction, then settled geometry.
        fixture.Window.UpdateLayout();
        fixture.Window.UpdateLayout();
        fixture.Window.UpdateLayout();
        fixture.Window.UpdateLayout();
        Assert.Same(before.Record, fixture.FirstVisible().Record);
        Assert.Equal(before.Y, fixture.FirstVisible().Y, 2);
        Assert.False(fixture.Anchor.HasPendingRestore);
    }

    [AvaloniaFact]
    public void EvictingTheAnchorReleasesThePendingRestore()
    {
        using var fixture = new ViewportFixture(false);
        fixture.Scroll.Offset = new Vector(0, 517);
        fixture.Window.UpdateLayout();
        var before = fixture.FirstVisible();
        fixture.Anchor.Capture();
        fixture.Items.Remove(before.Record);
        fixture.Window.UpdateLayout();
        fixture.Anchor.Restore();
        Assert.False(fixture.Anchor.HasPendingRestore);
    }

    [AvaloniaFact]
    public void ExistingScrollCorrectionIsNotAppliedTwiceWhenExtentIsUnchanged()
    {
        using var fixture = new ViewportFixture(false);
        fixture.Scroll.Offset = new Vector(0, 517);
        fixture.Window.UpdateLayout();
        var before = fixture.FirstVisible();
        double height = fixture.Items[^1].Height;
        HistoryViewSynchronizer.Synchronize(fixture.Items, new[] { new Record(height) }.Concat(fixture.Items).Take(100).ToArray(),
            fixture.Controller.HandleCollectionChangingForTest);
        fixture.Scroll.Offset += new Vector(0, height);
        fixture.Window.UpdateLayout();
        Assert.Same(before.Record, fixture.FirstVisible().Record);
        Assert.Equal(before.Y, fixture.FirstVisible().Y, 2);
        Assert.False(fixture.Anchor.HasPendingRestore);
    }

    private sealed class Record(double height)
    {
        public double Height { get; } = height;
    }

    private sealed class ViewportFixture : IDisposable
    {
        public ObservableCollection<Record> Items { get; } = new(Enumerable.Range(0, 100).Select(index => new Record(45 + index % 3 * 7)));
        public ItemsControl List { get; }
        public ScrollViewer Scroll { get; }
        public Window Window { get; }
        public ScrollViewportAnchor<Record> Anchor { get; }
        public ActivityHistoryViewportController Controller { get; }
        public ViewportFixture(bool virtualized)
        {
            List = virtualized ? new ListBox { AutoScrollToSelectedItem = false } : new ItemsControl();
            List.ItemsSource = Items;
            List.ItemTemplate = new FuncDataTemplate<Record>((item, _) => new Border { Height = item?.Height ?? 0, DataContext = item, Classes = { "history-record" } });
            Window = new Window { Width = 360, Height = 300 };
            if (virtualized) Window.Content = List;
            else Window.Content = new ScrollViewer { Content = new StackPanel { Children = { List } } };
            Window.Show(); Window.UpdateLayout();
            Scroll = Window.GetVisualDescendants().OfType<ScrollViewer>().First();
            Anchor = new ScrollViewportAnchor<Record>(() => Scroll, Rows, control => control.DataContext as Record,
                itemExists: item => Items.Any(candidate => ReferenceEquals(candidate, item)),
                realizeItem: virtualized ? item => ((ListBox)List).ScrollIntoView(item) : null);
            Controller = new ActivityHistoryViewportController(List, Anchor);
        }
        public IEnumerable<Control> Rows() => List.GetVisualDescendants().OfType<Border>().Where(row => row.Classes.Contains("history-record"));
        public (Record Record, double Y) FirstVisible()
        {
            var row = Rows().Select(row => (Row: row, Y: row.TranslatePoint(default, Scroll)!.Value.Y))
                .Where(row => row.Y + row.Row.Bounds.Height > 0 && row.Y < Scroll.Viewport.Height).OrderBy(row => row.Y).First();
            return ((Record)row.Row.DataContext!, row.Y);
        }
        public void Dispose() { Controller.Dispose(); Window.Close(); }
    }
}

internal static class HistoryViewportTestExtensions
{
    public static void HandleCollectionChangingForTest(this ActivityHistoryViewportController controller,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => controller.HandleCollectionChanging(null, args);
}
