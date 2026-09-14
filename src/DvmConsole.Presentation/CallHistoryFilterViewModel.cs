// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Presentation;

/// <summary>One bindable filter policy for desktop and mobile History pages.</summary>
public abstract class CallHistoryFilterViewModel : INotifyPropertyChanged
{
    private string callHistoryFilterText = string.Empty;
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

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private void ClearAdvancedHistoryFilters()
    {
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
        ClearAdvancedHistoryFilters();
    }

    public abstract void RefreshFilteredCallHistory();

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
        NotifyHistoryFilterChanged();
    }

    protected HistoryCatalogFilter CreateHistoryFilter()
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

    protected void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        NotifyPropertyChanged(propertyName);
    }

    protected void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
