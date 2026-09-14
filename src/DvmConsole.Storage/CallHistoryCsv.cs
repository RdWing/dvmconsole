// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using DvmConsole.Application;

namespace DvmConsole.Storage;

/// <summary>The established desktop history CSV format, shared by every host.</summary>
public static class CallHistoryCsv
{
    public static void Write(Stream destination, IEnumerable<CallHistoryExportRow> rows, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), 4096, leaveOpen);
        writer.WriteLine("Start,End,DurationSeconds,System,Channel,SourceId,Caller,Talkgroup,Protocol,Encryption,StreamId");
        foreach (var row in rows)
            writer.WriteLine(string.Join(",",
                Csv(row.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                Csv(row.EndedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(row.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(row.System), Csv(row.Channel), Csv(row.Source), Csv(row.Caller), Csv(row.Talkgroup),
                Csv(row.Protocol), Csv(row.Encryption), row.StreamId.ToString(CultureInfo.InvariantCulture)));
        writer.Flush();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
