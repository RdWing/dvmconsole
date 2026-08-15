using System.Globalization;

namespace DvmConsole.Desktop;

/// <summary>
/// Pure catalog filtering rules shared by the Recorder UI and tests. This
/// keeps operator filtering independent from the recording lifecycle and file
/// system implementation.
/// </summary>
public sealed record RecordingCatalogFilter(
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
    public bool Matches(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (Direction != "All" && !metadata.Direction.Equals(Direction, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Protocol != "All" && !metadata.Protocol.Equals(Protocol, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Encryption.Equals("Clear", StringComparison.OrdinalIgnoreCase) && metadata.IsEncrypted)
            return false;
        if (Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase) && !metadata.IsEncrypted)
            return false;
        if (!MatchesText(System, metadata.SystemName) ||
            !MatchesText(Channel, metadata.ChannelName) ||
            !MatchesText(Talkgroup, metadata.TalkgroupId?.ToString(CultureInfo.InvariantCulture)) ||
            !MatchesText(Subscriber, metadata.SubscriberId?.ToString(CultureInfo.InvariantCulture)) ||
            !MatchesText(Alias, metadata.SubscriberAlias))
        {
            return false;
        }

        DateTime localDate = metadata.UtcStartTime.ToLocalTime().Date;
        if (StartDate is DateTimeOffset startDate && localDate < startDate.Date)
            return false;
        if (EndDate is DateTimeOffset endDate && localDate > endDate.Date)
            return false;

        return MatchesText(SearchText,
            metadata.SystemName,
            metadata.ChannelName,
            metadata.Protocol,
            metadata.Direction,
            metadata.RecordingSourceType,
            metadata.FileName,
            metadata.RouteText,
            metadata.SubscriberAlias,
            metadata.SubscriberId?.ToString(CultureInfo.InvariantCulture),
            metadata.TalkgroupId?.ToString(CultureInfo.InvariantCulture));
    }

    private static bool MatchesText(string filter, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        string trimmed = filter.Trim();
        return values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
