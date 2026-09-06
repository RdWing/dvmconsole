// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class UiWorkCoalescingTests
{
    [Fact]
    public async Task SettingsBurstCapturesOnceAndFlushPersistsTheLatestState()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-settings-coalescing", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var settings = new UserSettings();
        var dispatcher = new QueuedTestUiDispatcher();
        int captures = 0;
        try
        {
            await using var persistence = new UserSettingsPersistenceCoordinator(store, settings,
                dispatcher: dispatcher, beforeCapture: () => captures++);
            for (int index = 0; index < 1000; index++)
            {
                settings.AudioInputGain = index / 1000d;
                persistence.Schedule();
            }
            Assert.Equal(0, captures);
            await persistence.FlushAsync();
            Assert.Equal(1, captures);
            Assert.Equal(0.999, store.Load().AudioInputGain);
            dispatcher.RunPending();
            Assert.Equal(1, captures);

            persistence.Schedule();
            await persistence.AdoptSnapshotAsync(store.CaptureSnapshot(new UserSettings { AudioInputGain = 0.25 }));
            dispatcher.RunPending();
            Assert.Equal(2, captures);
            Assert.Equal(0.25, store.Load().AudioInputGain);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CoalescedActionSupportsAnotherBurstAndStopsAfterDisposal()
    {
        var dispatcher = new QueuedTestUiDispatcher();
        int calls = 0;
        var action = new CoalescedUiAction(dispatcher, () => calls++);
        for (int index = 0; index < 1000; index++) action.Schedule();
        dispatcher.RunPending();
        Assert.Equal(1, calls);
        action.Schedule();
        await action.FlushAsync();
        dispatcher.RunPending();
        Assert.Equal(2, calls);
        action.Schedule();
        action.Dispose();
        dispatcher.RunPending();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FilteringPreservesOrderDuplicatesAndExistingRowsWithoutReset()
    {
        var source = new ObservableCollection<string>(["Alpha", "Beta", "Alpha", "Gamma"]);
        var filter = new FilteredPresetCollection<string>(source, item => item);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)filter.Items).CollectionChanged += (_, e) => changes.Add(e.Action);
        filter.FilterText = "Alpha";
        Assert.Equal(new[] { "Alpha", "Alpha" }, filter.Items);
        filter.FilterText = "";
        Assert.Equal(source, filter.Items);
        source.Move(3, 0);
        Assert.Equal(source, filter.Items);
        source.RemoveAt(1);
        Assert.Equal(source, filter.Items);
        source.Insert(2, "Delta");
        Assert.Equal(source, filter.Items);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }
}
