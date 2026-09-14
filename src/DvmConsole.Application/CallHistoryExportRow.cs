// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record CallHistoryExportRow(DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    TimeSpan? Duration, string System, string Channel, string Source, string Caller,
    string Talkgroup, string Protocol, string Encryption, uint StreamId)
{
    public static CallHistoryExportRow FromCall(ConsoleCallHistoryRecord call)
    {
        bool isEvent = call.Direction == ConsoleCallDirection.Event;
        return new(call.StartedAt, call.EndedAt, isEvent ? null : call.EndedAt - call.StartedAt,
            call.SystemName, isEvent ? call.EventSource : call.ChannelName,
            isEvent ? call.EventRid : call.SourceId.ToString(), call.Caller,
            isEvent ? call.EventTalkgroup : call.DestinationId.ToString(),
            isEvent ? "EVENT" : call.Protocol.ToString().ToUpperInvariant(),
            isEvent ? "—" : !call.Encryption.IsKnown ? "Unknown"
                : EncryptionProtocolLabels.StatusText(call.Encryption.IsSecure, call.Protocol, call.Encryption.AlgorithmId),
            call.PrimaryStreamId);
    }
}
