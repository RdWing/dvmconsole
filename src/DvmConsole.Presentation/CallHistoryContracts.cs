using System.Collections;

namespace DvmConsole.Presentation;

/// <summary>
/// Presentation-only projection of a call or event. Recording locations and
/// store handles remain owned by the host; the shared page only receives text
/// and asks the host to perform an action for the selected item.
/// </summary>
public interface ICallHistoryItemViewModel
{
    string TimestampText { get; }
    string DateText { get; }
    string DisplayChannelText { get; }
    string SystemName { get; }
    string DirectionText { get; }
    string ProtocolText { get; }
    string DurationText { get; }
    string RouteText { get; }
    string EncryptionText { get; }
    bool HasRecording { get; }
    bool HasPlayableRecording { get; }
    string RecordingFileName { get; }
    string RecordingDetailsText { get; }
}

/// <summary>
/// Bindable filter surface used by the shared History page. A desktop host may
/// adapt its existing collection while a future mobile host projects directly
/// from <c>IConsoleApplicationSession.History</c>.
/// </summary>
public interface ICallHistoryViewModel
{
    string CallHistoryFilterText { get; set; }
    IReadOnlyList<string> RecordingDirectionFilters { get; }
    IReadOnlyList<string> RecordingProtocolFilters { get; }
    IReadOnlyList<string> RecordingEncryptionFilters { get; }
    string RecordingDirectionFilter { get; set; }
    string RecordingProtocolFilter { get; set; }
    string RecordingEncryptionFilter { get; set; }
    string RecordingSystemFilterText { get; set; }
    string RecordingChannelFilterText { get; set; }
    string RecordingTalkgroupFilterText { get; set; }
    string RecordingSubscriberFilterText { get; set; }
    string RecordingAliasFilterText { get; set; }
    DateTimeOffset? RecordingStartDateFilter { get; set; }
    DateTimeOffset? RecordingEndDateFilter { get; set; }
    string HistoryFilterSummary { get; }
    IEnumerable FilteredCallHistory { get; }
}

public sealed class CallHistoryItemEventArgs(ICallHistoryItemViewModel item) : EventArgs
{
    public ICallHistoryItemViewModel Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}
