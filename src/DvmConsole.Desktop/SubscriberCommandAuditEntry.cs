using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

public sealed record SubscriberCommandAuditEntry(
    DateTimeOffset Timestamp,
    string SystemName,
    P25SubscriberCommand Command,
    uint DestinationId,
    bool Succeeded,
    string Detail)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string CommandText => Command switch
    {
        P25SubscriberCommand.CallAlert => "Page",
        P25SubscriberCommand.RadioCheck => "Radio check",
        P25SubscriberCommand.Inhibit => "Inhibit",
        P25SubscriberCommand.Uninhibit => "Uninhibit",
        _ => Command.ToString()
    };
    public string Summary => $"{TimestampText} · {SystemName} · {CommandText} RID {DestinationId}: {Detail}";
}
