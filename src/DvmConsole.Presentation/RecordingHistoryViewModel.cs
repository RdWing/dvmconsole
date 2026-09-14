// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using DvmConsole.Application;

namespace DvmConsole.Presentation;

public sealed class RecordingHistoryViewModel : HistoryProjectionViewModel<RecordingHistoryItemViewModel>
{
    public void Refresh(IReadOnlyList<RecordingArchiveEntry> recordings, Func<RecordingId, bool> isPlaying)
    {
        var existing = Entries.ToDictionary(item => item.Recording.Id);
        SetEntries(recordings.Select(record =>
        {
            if (!existing.TryGetValue(record.Id, out var item)) item = new(record);
            item.Update(record, isPlaying(record.Id));
            return item;
        }).ToArray());
    }

    public void RefreshPlayback(Func<RecordingId, bool> isPlaying)
    {
        foreach (var item in Entries) item.Update(item.Recording, isPlaying(item.Recording.Id));
    }
}

public sealed class RecordingHistoryItemViewModel(RecordingArchiveEntry recording) : IHistoryCatalogFilterItem, INotifyPropertyChanged
{
    public RecordingArchiveEntry Recording { get; private set; } = recording;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRecordingPlaying { get; private set; }
    internal void Update(RecordingArchiveEntry current, bool playing)
    {
        if (Recording == current && IsRecordingPlaying == playing) return;
        Recording = current;
        IsRecordingPlaying = playing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public DateTimeOffset Timestamp => Recording.StartedAt;
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string DateText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    public string DisplayChannelText => Recording.ChannelName;
    public string SystemName => Recording.SystemName;
    public string DirectionText => Recording.Direction;
    public string ProtocolText => Recording.Protocol;
    public string DurationText => CallDurationTextFormatter.Format(Recording.Duration);
    public string RouteText => Recording.Route;
    public string EncryptionText => Recording.EncryptionText;
    public bool HasRecording => true;
    public bool HasPlayableRecording => Recording.IsPlayable;
    public string RecordingPlaybackActionText => IsRecordingPlaying ? "Stop" : "Play";
    public string RecordingPlaybackHelpText => IsRecordingPlaying ? "Stop this recording." : "Play this recording.";
    public string RecordingFileName => Recording.FileName;
    public string RecordingDetailsText => Recording.Details;
    public bool IsEvent => false;
    public bool EncryptionKnown => Recording.EncryptionState != CallRecordingEncryptionState.Unknown;
    public bool Encrypted => Recording.EncryptionState == CallRecordingEncryptionState.Secure;
    public string DisplayDestinationText => Recording.Talkgroup;
    public string DisplaySourceText => Recording.Subscriber;
    public string CallerText => string.IsNullOrWhiteSpace(Recording.Alias) ? Recording.Subscriber : Recording.Alias;
    public string EventMessage => string.Empty;
    public IReadOnlyList<uint> StreamIds => Recording.StreamIds;
    public string? RecordingSubscriberAlias => Recording.Alias;
    public string? RecordingRouteText => Recording.Route;
}
