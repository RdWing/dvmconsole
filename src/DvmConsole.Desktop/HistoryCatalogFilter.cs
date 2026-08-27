using System.Globalization;

namespace DvmConsole.Desktop;

// One filter model for live events, calls, and completed TAR recordings.
public sealed record HistoryCatalogFilter(
    string SearchText = "",
    string Direction = "All",
    string Protocol = "All",
    string Encryption = "All",
    string System = "",
    string Channel = "",
    string Talkgroup = "",
    string Subscriber = "",
    string Alias = "",
    DateTimeOffset? StartDate = null,
    DateTimeOffset? EndDate = null)
{
    public bool IsUnfiltered =>
        string.IsNullOrWhiteSpace(SearchText) &&
        Direction == "All" &&
        Protocol == "All" &&
        Encryption == "All" &&
        string.IsNullOrWhiteSpace(System) &&
        string.IsNullOrWhiteSpace(Channel) &&
        string.IsNullOrWhiteSpace(Talkgroup) &&
        string.IsNullOrWhiteSpace(Subscriber) &&
        string.IsNullOrWhiteSpace(Alias) &&
        StartDate is null &&
        EndDate is null;

    public bool Matches(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsUnfiltered)
            return true;

        if (Direction != "All" && !entry.DirectionText.Equals(Direction, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Protocol != "All" && !entry.ProtocolText.Equals(Protocol, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Encryption.Equals("Clear", StringComparison.OrdinalIgnoreCase) && (entry.IsEvent || entry.Encrypted))
            return false;
        if (Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase) && (entry.IsEvent || !entry.Encrypted))
            return false;
        if (!MatchesText(System, entry.SystemName) ||
            !MatchesText(Channel, entry.DisplayChannelText) ||
            !MatchesText(Talkgroup, entry.DisplayDestinationText) ||
            !MatchesText(Subscriber, entry.DisplaySourceText) ||
            !MatchesEitherText(Alias, entry.CallerText, entry.Recording?.SubscriberAlias))
        {
            return false;
        }

        if (StartDate is not null || EndDate is not null)
        {
            DateTime localDate = entry.Timestamp.ToLocalTime().Date;
            if (StartDate is DateTimeOffset startDate && localDate < startDate.Date)
                return false;
            if (EndDate is DateTimeOffset endDate && localDate > endDate.Date)
                return false;
        }

        // Recording detail strings are intentionally assembled on demand for
        // display and search. Avoid materializing them for every retained TAR
        // row when only structured filters (or no filter) are active.
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        return SearchTextMatcher.MatchesAllTerms(
            SearchText,
            entry.SystemName,
            entry.DisplayChannelText,
            entry.EventMessage,
            entry.CallerText,
            entry.DisplaySourceText,
            entry.DisplayDestinationText,
            string.Join(' ', entry.StreamIds.Select(streamId =>
                streamId.ToString(CultureInfo.InvariantCulture))),
            entry.ProtocolText,
            entry.DirectionText,
            entry.EncryptionText,
            entry.RecordingFileName,
            entry.RecordingDetailsText,
            entry.Recording?.RouteText,
            entry.Recording?.SubscriberAlias);
    }

    private static bool MatchesText(string filter, string? value)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        string trimmed = filter.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesEitherText(
        string filter,
        string? first,
        string? second)
        => string.IsNullOrWhiteSpace(filter) ||
           MatchesText(filter, first) ||
           MatchesText(filter, second);
}
