using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal sealed class HistoryRecordingWorkspace : INotifyPropertyChanged
{
    private readonly object recordingCatalogScanSync = new();
    private readonly ObservableCollection<CallHistoryEntry> filteredCallHistoryEntries = [];
    private readonly ObservableCollection<CallHistoryEntry> activityCallHistoryEntries = [];
    private readonly ResettableObservableCollection<CallRecordingMetadata> recordingEntries = [];
    private string recordingRetentionDaysText;
    private string recordingRootPathText;
    private string callHistoryFilterText = string.Empty;
    private string recordingFilterText = string.Empty;
    private string recordingDirectionFilter = "All";
    private string recordingProtocolFilter = "All";
    private string recordingEncryptionFilter = "All";
    private string recordingSystemFilterText = string.Empty;
    private string recordingChannelFilterText = string.Empty;
    private string recordingTalkgroupFilterText = string.Empty;
    private string recordingSubscriberFilterText = string.Empty;
    private string recordingAliasFilterText = string.Empty;
    private DateTimeOffset? recordingStartDateFilter;
    private DateTimeOffset? recordingEndDateFilter;
    private bool recordingTimeColumnVisible = true;
    private bool recordingDurationColumnVisible = true;
    private bool recordingChannelColumnVisible = true;
    private bool recordingTalkgroupColumnVisible = true;
    private bool recordingSourceIdColumnVisible = true;
    private bool recordingAliasColumnVisible = true;
    private bool recordingDirectionColumnVisible;
    private bool recordingProtocolColumnVisible;
    private bool recordingSystemColumnVisible;
    private bool recordingEncryptionColumnVisible;
    private bool recordingDiagnosticsColumnVisible = true;
    private CancellationTokenSource? recordingCatalogScanCancellation;
    private int recordingCatalogScanGeneration;
    private long recordingCatalogMutationRevision;
    private Task recordingCatalogScanTask = Task.CompletedTask;

    public HistoryRecordingWorkspace(
        string recordingRetentionDaysText,
        string recordingRootPathText)
    {
        this.recordingRetentionDaysText = recordingRetentionDaysText;
        this.recordingRootPathText = recordingRootPathText;
        CallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(History.Entries);
        FilteredCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(filteredCallHistoryEntries);
        ActivityCallHistory = new ReadOnlyObservableCollection<CallHistoryEntry>(activityCallHistoryEntries);
        Recordings = new ReadOnlyObservableCollection<CallRecordingMetadata>(recordingEntries);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal CallHistoryStore History { get; } = new();
    internal ObservableCollection<CallRecordingMetadata> RecordingEntries => recordingEntries;

    internal void ReplaceRecordingEntries(IEnumerable<CallRecordingMetadata> recordings)
        => recordingEntries.ReplaceAll(recordings);

    public ReadOnlyObservableCollection<CallHistoryEntry> CallHistory { get; }
    public ReadOnlyObservableCollection<CallHistoryEntry> FilteredCallHistory { get; }
    public ReadOnlyObservableCollection<CallHistoryEntry> ActivityCallHistory { get; }
    public ReadOnlyObservableCollection<CallRecordingMetadata> Recordings { get; }

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

    public string CallHistoryFilterText
    {
        get => callHistoryFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (callHistoryFilterText == normalized)
                return;
            callHistoryFilterText = normalized;
            NotifyPropertyChanged();
            RefreshFilteredCallHistory();
        }
    }

    public string RecordingFilterText
    {
        get => recordingFilterText;
        set
        {
            string normalized = value ?? string.Empty;
            if (recordingFilterText == normalized)
                return;
            recordingFilterText = normalized;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(FilteredRecordings));
        }
    }

    public IReadOnlyList<string> RecordingDirectionFilters { get; } = ["All", "RX", "TX"];
    public IReadOnlyList<string> RecordingProtocolFilters { get; } = ["All", "DMR", "P25", "ANALOG", "NXDN"];
    public IReadOnlyList<string> RecordingEncryptionFilters { get; } = ["All", "Clear", "Encrypted"];

    public string RecordingDirectionFilter
    {
        get => recordingDirectionFilter;
        set => SetRecordingFilter(ref recordingDirectionFilter, value);
    }

    public string RecordingProtocolFilter
    {
        get => recordingProtocolFilter;
        set => SetRecordingFilter(ref recordingProtocolFilter, value);
    }

    public string RecordingEncryptionFilter
    {
        get => recordingEncryptionFilter;
        set => SetRecordingFilter(ref recordingEncryptionFilter, value);
    }

    public string RecordingSystemFilterText
    {
        get => recordingSystemFilterText;
        set => SetRecordingFilter(ref recordingSystemFilterText, value, allowEmpty: true);
    }

    public string RecordingChannelFilterText
    {
        get => recordingChannelFilterText;
        set => SetRecordingFilter(ref recordingChannelFilterText, value, allowEmpty: true);
    }

    public string RecordingTalkgroupFilterText
    {
        get => recordingTalkgroupFilterText;
        set => SetRecordingFilter(ref recordingTalkgroupFilterText, value, allowEmpty: true);
    }

    public string RecordingSubscriberFilterText
    {
        get => recordingSubscriberFilterText;
        set => SetRecordingFilter(ref recordingSubscriberFilterText, value, allowEmpty: true);
    }

    public string RecordingAliasFilterText
    {
        get => recordingAliasFilterText;
        set => SetRecordingFilter(ref recordingAliasFilterText, value, allowEmpty: true);
    }

    public DateTimeOffset? RecordingStartDateFilter
    {
        get => recordingStartDateFilter;
        set => SetRecordingDateFilter(ref recordingStartDateFilter, value);
    }

    public DateTimeOffset? RecordingEndDateFilter
    {
        get => recordingEndDateFilter;
        set => SetRecordingDateFilter(ref recordingEndDateFilter, value);
    }

    public bool ShowRecordingTimeColumn
    {
        get => recordingTimeColumnVisible;
        set => SetField(ref recordingTimeColumnVisible, value);
    }

    public bool ShowRecordingDurationColumn
    {
        get => recordingDurationColumnVisible;
        set => SetField(ref recordingDurationColumnVisible, value);
    }

    public bool ShowRecordingChannelColumn
    {
        get => recordingChannelColumnVisible;
        set => SetField(ref recordingChannelColumnVisible, value);
    }

    public bool ShowRecordingTalkgroupColumn
    {
        get => recordingTalkgroupColumnVisible;
        set => SetField(ref recordingTalkgroupColumnVisible, value);
    }

    public bool ShowRecordingSourceIdColumn
    {
        get => recordingSourceIdColumnVisible;
        set => SetField(ref recordingSourceIdColumnVisible, value);
    }

    public bool ShowRecordingAliasColumn
    {
        get => recordingAliasColumnVisible;
        set => SetField(ref recordingAliasColumnVisible, value);
    }

    public bool ShowRecordingDirectionColumn
    {
        get => recordingDirectionColumnVisible;
        set => SetField(ref recordingDirectionColumnVisible, value);
    }

    public bool ShowRecordingProtocolColumn
    {
        get => recordingProtocolColumnVisible;
        set => SetField(ref recordingProtocolColumnVisible, value);
    }

    public bool ShowRecordingSystemColumn
    {
        get => recordingSystemColumnVisible;
        set => SetField(ref recordingSystemColumnVisible, value);
    }

    public bool ShowRecordingEncryptionColumn
    {
        get => recordingEncryptionColumnVisible;
        set => SetField(ref recordingEncryptionColumnVisible, value);
    }

    public bool ShowRecordingDiagnosticsColumn
    {
        get => recordingDiagnosticsColumnVisible;
        set => SetField(ref recordingDiagnosticsColumnVisible, value);
    }

    public bool HasAdvancedHistoryFilters =>
        RecordingDirectionFilter != "All" ||
        RecordingProtocolFilter != "All" ||
        RecordingEncryptionFilter != "All" ||
        !string.IsNullOrWhiteSpace(RecordingSystemFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingChannelFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingTalkgroupFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingSubscriberFilterText) ||
        !string.IsNullOrWhiteSpace(RecordingAliasFilterText) ||
        RecordingStartDateFilter is not null ||
        RecordingEndDateFilter is not null;

    public string HistoryFilterSummary
    {
        get
        {
            var filters = new List<string>();
            if (RecordingDirectionFilter != "All") filters.Add(RecordingDirectionFilter);
            if (RecordingProtocolFilter != "All") filters.Add(RecordingProtocolFilter);
            if (RecordingEncryptionFilter != "All") filters.Add(RecordingEncryptionFilter);
            if (!string.IsNullOrWhiteSpace(RecordingSystemFilterText)) filters.Add($"system {RecordingSystemFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingChannelFilterText)) filters.Add($"channel {RecordingChannelFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingTalkgroupFilterText)) filters.Add($"TG {RecordingTalkgroupFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingSubscriberFilterText)) filters.Add($"RID {RecordingSubscriberFilterText}");
            if (!string.IsNullOrWhiteSpace(RecordingAliasFilterText)) filters.Add($"alias {RecordingAliasFilterText}");
            if (RecordingStartDateFilter is DateTimeOffset start) filters.Add($"from {start:yyyy-MM-dd}");
            if (RecordingEndDateFilter is DateTimeOffset end) filters.Add($"to {end:yyyy-MM-dd}");
            return string.Join(" · ", filters);
        }
    }

    public IReadOnlyList<CallRecordingMetadata> FilteredRecordings
        => Recordings
            .Where(metadata => new RecordingCatalogFilter(
                RecordingFilterText,
                RecordingDirectionFilter,
                RecordingProtocolFilter,
                RecordingEncryptionFilter,
                RecordingSystemFilterText,
                RecordingChannelFilterText,
                RecordingTalkgroupFilterText,
                RecordingSubscriberFilterText,
                RecordingAliasFilterText,
                RecordingStartDateFilter,
                RecordingEndDateFilter).Matches(metadata))
            .ToArray();

    public void ResetRecordingColumns()
    {
        ShowRecordingTimeColumn = true;
        ShowRecordingDurationColumn = true;
        ShowRecordingChannelColumn = true;
        ShowRecordingTalkgroupColumn = true;
        ShowRecordingSourceIdColumn = true;
        ShowRecordingAliasColumn = true;
        ShowRecordingDirectionColumn = false;
        ShowRecordingProtocolColumn = false;
        ShowRecordingSystemColumn = false;
        ShowRecordingEncryptionColumn = false;
        ShowRecordingDiagnosticsColumn = true;
    }

    public void ClearRecordingFilters()
    {
        RecordingFilterText = string.Empty;
        RecordingDirectionFilter = "All";
        RecordingProtocolFilter = "All";
        RecordingEncryptionFilter = "All";
        RecordingSystemFilterText = string.Empty;
        RecordingChannelFilterText = string.Empty;
        RecordingTalkgroupFilterText = string.Empty;
        RecordingSubscriberFilterText = string.Empty;
        RecordingAliasFilterText = string.Empty;
        RecordingStartDateFilter = null;
        RecordingEndDateFilter = null;
    }

    public void ClearHistoryFilters()
    {
        CallHistoryFilterText = string.Empty;
        ClearRecordingFilters();
    }

    public void RefreshFilteredCallHistory()
        => HistoryViewSynchronizer.Synchronize(
            filteredCallHistoryEntries,
            CallHistory.Where(CreateHistoryFilter().Matches));

    public void RefreshActivityCallHistory(IEnumerable<CallHistoryEntry> entries)
        => HistoryViewSynchronizer.Synchronize(activityCallHistoryEntries, entries);

    public void NotifyRecordingsChanged()
        => NotifyPropertyChanged(nameof(FilteredRecordings));

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

    private void SetRecordingDateFilter(
        ref DateTimeOffset? field,
        DateTimeOffset? value,
        [CallerMemberName] string? propertyName = null)
    {
        DateTimeOffset? normalized = value is DateTimeOffset date
            ? new DateTimeOffset(date.Date, date.Offset)
            : null;
        if (field == normalized)
            return;
        field = normalized;
        NotifyPropertyChanged(propertyName);
        NotifyPropertyChanged(nameof(FilteredRecordings));
        NotifyHistoryFilterChanged();
    }

    private void SetRecordingFilter(
        ref string field,
        string? value,
        bool allowEmpty = false,
        [CallerMemberName] string? propertyName = null)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? (allowEmpty ? string.Empty : "All")
            : value.Trim();
        if (field.Equals(normalized, StringComparison.Ordinal))
            return;
        field = normalized;
        NotifyPropertyChanged(propertyName);
        NotifyPropertyChanged(nameof(FilteredRecordings));
        NotifyHistoryFilterChanged();
    }

    private HistoryCatalogFilter CreateHistoryFilter()
        => new(
            CallHistoryFilterText,
            RecordingDirectionFilter,
            RecordingProtocolFilter,
            RecordingEncryptionFilter,
            RecordingSystemFilterText,
            RecordingChannelFilterText,
            RecordingTalkgroupFilterText,
            RecordingSubscriberFilterText,
            RecordingAliasFilterText,
            RecordingStartDateFilter,
            RecordingEndDateFilter);

    private void NotifyHistoryFilterChanged()
    {
        RefreshFilteredCallHistory();
        NotifyPropertyChanged(nameof(HasAdvancedHistoryFilters));
        NotifyPropertyChanged(nameof(HistoryFilterSummary));
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        NotifyPropertyChanged(propertyName);
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record RecordingCatalogScanSnapshot(
    int Generation,
    long MutationRevision,
    CancellationToken CancellationToken);

internal sealed record RecordingCatalogScanShutdown(
    Task Scan,
    CancellationTokenSource? Cancellation);

internal static class HistoryViewSynchronizer
{
    public static void Synchronize(
        ObservableCollection<CallHistoryEntry> target,
        IEnumerable<CallHistoryEntry> desiredEntries)
    {
        CallHistoryEntry[] desired = desiredEntries.ToArray();
        var desiredSet = new HashSet<CallHistoryEntry>(desired, ReferenceEqualityComparer.Instance);
        lock (target)
        {
            for (int index = target.Count - 1; index >= 0; index--)
            {
                if (!desiredSet.Contains(target[index]))
                    target.RemoveAt(index);
            }

            for (int index = 0; index < desired.Length; index++)
            {
                if (index < target.Count && ReferenceEquals(target[index], desired[index]))
                    continue;
                int existingIndex = target.IndexOf(desired[index]);
                if (existingIndex >= 0)
                    target.Move(existingIndex, index);
                else
                    target.Insert(index, desired[index]);
            }
        }
    }
}
