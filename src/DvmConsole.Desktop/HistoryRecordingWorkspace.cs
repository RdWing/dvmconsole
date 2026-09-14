// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DvmConsole.Desktop;

internal sealed class HistoryRecordingController : DvmConsole.Presentation.CallHistoryFilterViewModel
{
    internal const int MaximumActivityEntries = 100;
    private readonly object recordingCatalogScanSync = new();
    private readonly ObservableCollection<CallHistoryEntry> filteredCallHistoryEntries = [];
    private readonly ObservableCollection<CallHistoryEntry> activityCallHistoryEntries = [];
    private readonly ResettableObservableCollection<CallRecordingMetadata> recordingEntries = [];
    private string recordingRetentionDaysText;
    private string recordingRootPathText;
    private bool activityCurrentZoneOnly;
    private bool activityReceiveEnabledOnly = true;
    private CancellationTokenSource? recordingCatalogScanCancellation;
    private int recordingCatalogScanGeneration;
    private long recordingCatalogMutationRevision;
    private Task recordingCatalogScanTask = Task.CompletedTask;

    public HistoryRecordingController(
        string recordingRetentionDaysText,
        string recordingRootPathText,
        ConsoleCallHistory? history = null)
    {
        History = history is null ? new() : new(history);
        this.recordingRetentionDaysText = recordingRetentionDaysText;
        this.recordingRootPathText = recordingRootPathText;
        CallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(History.Entries);
        FilteredCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(filteredCallHistoryEntries);
        ActivityCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(activityCallHistoryEntries);
        Recordings = new ReadOnlyObservableCollection<CallRecordingMetadata>(recordingEntries);
    }

    internal event NotifyCollectionChangedEventHandler? FilteredCallHistoryChanging;
    internal event NotifyCollectionChangedEventHandler? ActivityCallHistoryChanging;

    internal CallHistoryStore History { get; }
    internal ObservableCollection<CallRecordingMetadata> RecordingEntries => recordingEntries;

    internal void ReplaceRecordingEntries(IEnumerable<CallRecordingMetadata> recordings)
        => recordingEntries.ReplaceAll(recordings);

    public ReadOnlyObservableCollection<CallHistoryEntry> CallHistory { get; }
    public ReadOnlyObservableCollection<CallHistoryEntry> FilteredCallHistory { get; }
    public ReadOnlyObservableCollection<CallHistoryEntry> ActivityCallHistory { get; }
    public ReadOnlyObservableCollection<CallRecordingMetadata> Recordings { get; }
    public bool ActivityCurrentZoneOnly => activityCurrentZoneOnly;
    public bool ActivityReceiveEnabledOnly => activityReceiveEnabledOnly;
    public string ActivityZoneFilterButtonText => ActivityCurrentZoneOnly ? "Zone Wide" : "System Wide";
    public string ActivityReceiveFilterButtonText => ActivityReceiveEnabledOnly ? "Active" : "All";

    public string RecordingRetentionDaysText
    {
        get => recordingRetentionDaysText;
        set => SetField(ref recordingRetentionDaysText, value ?? string.Empty);
    }

    public string RecordingRootPathText
    {
        get => recordingRootPathText;
        set => SetField(ref recordingRootPathText, value ?? string.Empty);
    }

    public override void RefreshFilteredCallHistory()
    {
        HistoryCatalogFilter filter = CreateHistoryFilter();
        IEnumerable<CallHistoryEntry> desiredEntries = filter.IsUnfiltered
            ? CallHistory
            : CallHistory.Where(filter.Matches);
        HistoryViewSynchronizer.Synchronize(
            filteredCallHistoryEntries,
            desiredEntries,
            args => FilteredCallHistoryChanging?.Invoke(this, args));
    }

    public void RefreshActivityCallHistory(IEnumerable<CallHistoryEntry> entries)
    {
        HistoryViewSynchronizer.Synchronize(
            activityCallHistoryEntries,
            entries,
            args => ActivityCallHistoryChanging?.Invoke(this, args));

        // Update in place after synchronization so viewport anchoring observes
        // the old row geometry before an insertion changes the date headers.
        string? previousDate = null;
        foreach (CallHistoryEntry entry in activityCallHistoryEntries)
        {
            string date = entry.DateText;
            entry.SetStartsActivityDay(date != previousDate);
            previousDate = date;
        }
    }

    public void ToggleActivityZoneFilter()
    {
        activityCurrentZoneOnly = !activityCurrentZoneOnly;
        NotifyPropertyChanged(nameof(ActivityCurrentZoneOnly));
        NotifyPropertyChanged(nameof(ActivityZoneFilterButtonText));
    }

    public void ToggleActivityReceiveFilter()
    {
        activityReceiveEnabledOnly = !activityReceiveEnabledOnly;
        NotifyPropertyChanged(nameof(ActivityReceiveEnabledOnly));
        NotifyPropertyChanged(nameof(ActivityReceiveFilterButtonText));
    }

    public void RefreshActivityCallHistory(
        string? selectedSystemName,
        IEnumerable<string>? selectedZoneChannelNames,
        IEnumerable<string>? receiveEnabledChannelNames)
        => RefreshActivityCallHistory(SelectActivityHistory(
            CallHistory,
            selectedSystemName,
            ActivityCurrentZoneOnly ? selectedZoneChannelNames : null,
            ActivityReceiveEnabledOnly ? receiveEnabledChannelNames : null));

    internal static CallHistoryEntry[] SelectActivityHistory(
        IEnumerable<CallHistoryEntry> history,
        string? selectedSystemName,
        IEnumerable<string>? selectedZoneChannelNames,
        IEnumerable<string>? receiveEnabledChannelNames = null)
    {
        if (selectedSystemName is null)
            return [];

        HashSet<string>? selectedChannels = selectedZoneChannelNames is null
            ? null
            : new HashSet<string>(selectedZoneChannelNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? receiveEnabledChannels = receiveEnabledChannelNames is null
            ? null
            : new HashSet<string>(receiveEnabledChannelNames, StringComparer.OrdinalIgnoreCase);
        return history
            .Where(entry => entry.SystemName.Equals(selectedSystemName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => selectedChannels is null || selectedChannels.Contains(entry.ChannelName))
            .Where(entry => receiveEnabledChannels is null || receiveEnabledChannels.Contains(entry.ChannelName))
            .Take(MaximumActivityEntries)
            .ToArray();
    }

    public RecordingCatalogScanSnapshot BeginRecordingCatalogScan()
    {
        var cancellation = new CancellationTokenSource();
        lock (recordingCatalogScanSync)
        {
            recordingCatalogScanCancellation?.Cancel();
            recordingCatalogScanCancellation?.Dispose();
            recordingCatalogScanCancellation = cancellation;
            return new RecordingCatalogScanSnapshot(
                ++recordingCatalogScanGeneration,
                recordingCatalogMutationRevision,
                cancellation.Token);
        }
    }

    public void PublishRecordingCatalogScan(
        RecordingCatalogScanSnapshot snapshot,
        Task scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (recordingCatalogScanSync)
        {
            if (snapshot.Generation == recordingCatalogScanGeneration)
                recordingCatalogScanTask = scan;
        }
    }

    public bool ShouldRestartRecordingCatalogScan(RecordingCatalogScanSnapshot snapshot)
    {
        lock (recordingCatalogScanSync)
        {
            return snapshot.Generation == recordingCatalogScanGeneration &&
                snapshot.MutationRevision != recordingCatalogMutationRevision;
        }
    }

    public bool TryApplyRecordingCatalogSnapshot(
        RecordingCatalogScanSnapshot snapshot,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (recordingCatalogScanSync)
        {
            if (snapshot.CancellationToken.IsCancellationRequested ||
                snapshot.Generation != recordingCatalogScanGeneration ||
                snapshot.MutationRevision != recordingCatalogMutationRevision)
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal static bool IsRecordingCatalogSnapshotCurrent(
        int snapshotGeneration,
        int currentGeneration,
        long snapshotMutationRevision,
        long currentMutationRevision,
        bool isCancellationRequested)
        => !isCancellationRequested &&
           snapshotGeneration == currentGeneration &&
           snapshotMutationRevision == currentMutationRevision;

    public void RecordRecordingCatalogMutation()
    {
        lock (recordingCatalogScanSync)
            recordingCatalogMutationRevision++;
    }

    public RecordingCatalogScanShutdown CancelRecordingCatalogScan()
    {
        lock (recordingCatalogScanSync)
        {
            CancellationTokenSource? cancellation = recordingCatalogScanCancellation;
            cancellation?.Cancel();
            recordingCatalogScanCancellation = null;
            return new RecordingCatalogScanShutdown(recordingCatalogScanTask, cancellation);
        }
    }


}

internal sealed record RecordingCatalogScanSnapshot(
    int Generation,
    long MutationRevision,
    CancellationToken CancellationToken);

internal sealed record RecordingCatalogScanShutdown(
    Task Scan,
    CancellationTokenSource? Cancellation);
