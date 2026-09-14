// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

/// <summary>Projects authoritative session history through the shared History filters and view.</summary>
public sealed class ConsoleCallHistoryViewModel : HistoryProjectionViewModel<ConsoleCallHistoryItemViewModel>
{
    private Dictionary<CallId, ConsoleCallHistoryItemViewModel> byId = [];

    private IReadOnlyList<RecordingArchiveEntry>? catalog;

    public void Refresh(IReadOnlyList<ConsoleCallHistoryRecord> records,
        IReadOnlyList<RecordingArchiveEntry>? recordings = null, Func<RecordingId, bool>? isPlaying = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (ReferenceEquals(catalog, recordings) && Entries.Count == records.Count && Entries.Select(entry => entry.Record).SequenceEqual(records))
        {
            foreach (var item in Entries) item.Attach(item.Recording, isPlaying);
            return;
        }
        catalog = recordings;
        var attachments = new Dictionary<CallId, RecordingArchiveEntry>();
        if (recordings is { Count: > 0 })
        {
            var index = new RecordingCallIndex(records);
            foreach (var recording in recordings)
                if (recording.CallIdentity is { } identity && index.FindBest(identity) is { } id)
                    attachments.TryAdd(id, recording);
        }
        var next = new List<ConsoleCallHistoryItemViewModel>(records.Count);
        foreach (var record in records)
        {
            if (!byId.TryGetValue(record.Id, out var entry)) entry = new(record);
            else entry.Update(record);
            entry.Attach(attachments.GetValueOrDefault(record.Id), isPlaying);
            next.Add(entry);
        }
        byId = next.ToDictionary(entry => entry.Record.Id);
        SetEntries(next);
    }
}

public sealed class ConsoleCallHistoryItemViewModel : IHistoryCatalogFilterItem, INotifyPropertyChanged
{
    private CallHistoryExportRow text;
    public ConsoleCallHistoryItemViewModel(ConsoleCallHistoryRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        text = CallHistoryExportRow.FromCall(record);
    }

    public ConsoleCallHistoryRecord Record { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(ConsoleCallHistoryRecord record)
    {
        if (ReferenceEquals(Record, record)) return;
        Record = record;
        text = CallHistoryExportRow.FromCall(record);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public DateTimeOffset Timestamp => Record.StartedAt;
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string DateText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    public string SystemName => text.System;
    public string DisplayChannelText => text.Channel;
    public string DisplaySourceText => text.Source;
    public string DisplayDestinationText => text.Talkgroup;
    public string CallerText => text.Caller;
    public string ProtocolText => text.Protocol;
    public string EncryptionText => text.Encryption;
    public bool IsEvent => Record.Direction == ConsoleCallDirection.Event;
    public bool EncryptionKnown => !IsEvent && Record.Encryption.IsKnown;
    public bool Encrypted => !IsEvent && Record.Encryption.IsSecure;
    public string EventMessage => Record.EventMessage;
    public IReadOnlyList<uint> StreamIds => Record.StreamIds;
    public string DirectionText => IsEvent ? "EVENT" : Record.Direction == ConsoleCallDirection.Transmit ? "TX" : "RX";
    public string RouteText => IsEvent ? EventMessage : $"{CallerText} → TG {Record.DestinationId}";
    public string DurationText => text.Duration is { } duration ? CallDurationTextFormatter.Format(duration) : IsEvent ? "—" : "Active";

    public RecordingArchiveEntry? Recording { get; private set; }
    public bool IsRecordingPlaying { get; private set; }
    internal void Attach(RecordingArchiveEntry? recording, Func<RecordingId, bool>? isPlaying)
    {
        bool playing = recording is not null && isPlaying?.Invoke(recording.Id) == true;
        if (Recording == recording && IsRecordingPlaying == playing) return;
        Recording = recording;
        IsRecordingPlaying = playing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public bool HasRecording => Recording is not null;
    public bool HasPlayableRecording => Recording?.IsPlayable == true;
    public string RecordingPlaybackActionText => IsRecordingPlaying ? "Stop" : "Play";
    public string RecordingPlaybackHelpText => Recording is null ? "No recording is attached to this history entry."
        : IsRecordingPlaying ? "Stop this recording." : "Play this recording.";
    public string RecordingFileName => Recording?.FileName ?? string.Empty;
    public string RecordingDetailsText => Recording?.Details ?? string.Empty;
    public string? RecordingSubscriberAlias => Recording?.Alias;
    public string? RecordingRouteText => Recording?.Route;
}
