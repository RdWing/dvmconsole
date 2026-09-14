// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal sealed record RecordingCatalogReconciliationMetrics(
    long ExistingEntryVisits,
    long DesiredRecordingVisits,
    long KeyLookups,
    long IdentityCandidateVisits,
    long MergeVisits)
{
    public long TotalWork => ExistingEntryVisits + DesiredRecordingVisits +
        KeyLookups + IdentityCandidateVisits + MergeVisits;
}

// One inbound voice stream recorded by the dispatch shell.
public sealed class CallHistoryEntry : INotifyPropertyChanged, DvmConsole.Presentation.IHistoryCatalogFilterItem
{
    private readonly List<uint> streamIds;
    private DateTimeOffset? endTimestamp;
    private EncryptionSnapshot encryption;
    private readonly bool isEvent;
    private readonly bool isConsoleTransmission;
    private readonly bool isRecordingOnly;
    private readonly string eventSource;
    private readonly string eventMessage;
    private readonly string eventRidText;
    private readonly string eventTgidText;
    private CallRecordingMetadata? recording;
    private bool isRecordingPlaying;
    private bool startsActivityDay;

    public CallHistoryEntry(
        DateTimeOffset timestamp,
        string systemName,
        string channelName,
        uint sourceId,
        uint destinationId,
        FneTrafficProtocol protocol,
        uint streamId,
        string? callerText = null,
        bool encrypted = false,
        bool isEvent = false,
        bool isConsoleTransmission = false,
        bool isRecordingOnly = false,
        string? eventSource = null,
        string? eventMessage = null,
        string? eventRidText = null,
        string? eventTgidText = null,
        long? receiveEpisodeId = null,
        bool encryptionKnown = false,
        CallId? callId = null,
        ChannelId? channelId = null)
    {
        Id = callId ?? CallId.New();
        ChannelId = channelId;
        Timestamp = timestamp;
        SystemName = systemName;
        ChannelName = channelName;
        SourceId = sourceId;
        DestinationId = destinationId;
        Protocol = protocol;
        StreamId = streamId;
        CallerText = string.IsNullOrWhiteSpace(callerText) ? sourceId.ToString() : callerText.Trim();
        encryption = encryptionKnown
            ? EncryptionSnapshot.FromStored(
                encrypted
                    ? CallRecordingEncryptionState.Secure
                    : CallRecordingEncryptionState.Clear)
            : EncryptionSnapshot.Unknown;
        this.isEvent = isEvent;
        this.isConsoleTransmission = isConsoleTransmission;
        this.isRecordingOnly = isRecordingOnly;
        this.eventSource = eventSource?.Trim() ?? string.Empty;
        this.eventMessage = eventMessage?.Trim() ?? string.Empty;
        this.eventRidText = eventRidText?.Trim() ?? string.Empty;
        this.eventTgidText = eventTgidText?.Trim() ?? string.Empty;
        ReceiveEpisodeId = receiveEpisodeId;
        streamIds = streamId == 0 ? [] : [streamId];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public CallId Id { get; }
    public ChannelId? ChannelId { get; }
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset? EndTimestamp => endTimestamp;
    public string SystemName { get; }
    public string ChannelName { get; }
    public uint SourceId { get; }
    public uint DestinationId { get; }
    public FneTrafficProtocol Protocol { get; }
    public uint StreamId { get; }
    internal long? ReceiveEpisodeId { get; }
    public IReadOnlyList<uint> StreamIds => streamIds;
    public int StreamFragmentCount => streamIds.Count;
    public string CallerText { get; }
    public bool IsEvent => isEvent;
    public bool IsConsoleTransmission => isConsoleTransmission;
    public bool IsRecordingOnly => isRecordingOnly;
    public string DirectionText => IsEvent ? "EVENT" : IsConsoleTransmission ? "TX" : "RX";
    public string EventSource => eventSource;
    public string EventMessage => eventMessage;
    public string EventRidText => eventRidText;
    public string EventTgidText => eventTgidText;
    public bool Encrypted => !IsEvent && encryption.IsSecure;
    public bool EncryptionKnown => !IsEvent && encryption.IsKnown;
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string DateText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    public bool StartsActivityDay => startsActivityDay;
    public string ProtocolText => IsEvent ? "EVENT" : Protocol.ToString().ToUpperInvariant();
    public string DisplayChannelText => IsEvent ? EventSource : ChannelName;
    public string DisplaySourceText => IsEvent ? EventRidText : SourceId.ToString();
    public string DisplayDestinationText => IsEvent ? EventTgidText : DestinationId.ToString();
    public string RouteText => IsEvent ? EventMessage : $"{CallerText} → TG {DestinationId}";
    public string StreamText => IsEvent
        ? "Event"
        : StreamFragmentCount > 1
            ? $"{ProtocolText} · {StreamFragmentCount} stream fragments"
            : $"{ProtocolText} · stream {StreamId}";
    public bool IsActive => !IsEvent && endTimestamp is null;
    public TimeSpan? Duration => IsEvent
        ? null
        : IsRecordingOnly && recording is not null
            ? TimeSpan.FromMilliseconds(Math.Max(0, recording.DurationMs))
            : endTimestamp - Timestamp ?? (recording is null
                ? null
                : TimeSpan.FromMilliseconds(Math.Max(0, recording.DurationMs)));
    public string DurationText => Duration is TimeSpan duration
        ? CallDurationTextFormatter.Format(duration)
        : IsEvent ? "—" : "Active";
    public byte? EncryptionAlgorithmId => encryption.AlgorithmId;
    public ushort? EncryptionKeyId => encryption.KeyId;
    public string EncryptionText => IsEvent
        ? "—"
        : !EncryptionKnown
            ? "Unknown"
        : EncryptionPresentation.StatusText(Encrypted, Protocol, encryption.AlgorithmId);

    public bool HasRecording => recording is not null;
    public bool HasPlayableRecording => recording?.IsPlayable == true;
    public bool IsRecordingPlaying => isRecordingPlaying;
    public string RecordingPlaybackActionText => IsRecordingPlaying
        ? "Stop"
        : "Play";
    public string RecordingPlaybackHelpText => IsRecordingPlaying
        ? "Stop playback of this TAR recording"
        : HasPlayableRecording
            ? "Play this validated TAR recording"
            : "Playback is unavailable because the TAR recording is missing or invalid";
    public string RecordingPlaybackToolTip => IsRecordingPlaying
        ? "Stop TAR recording playback"
        : "Play validated TAR recording";
    public CallRecordingMetadata? Recording => recording;
    public string RecordingFileName => recording?.FileName ?? string.Empty;
    public string RecordingDetailsText => recording?.TechnicalDetailsText ?? string.Empty;
    public string? RecordingSubscriberAlias => recording?.SubscriberAlias;
    public string? RecordingRouteText => recording?.RouteText;
    public string RecordingPath => recording?.FilePath ?? string.Empty;

    public void SetRecording(CallRecordingMetadata? value)
    {
        if (ReferenceEquals(recording, value))
            return;
        recording = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlayableRecording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPlaybackHelpText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingFileName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingDetailsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPath)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
    }

    internal void SetRecordingPlaying(bool value)
    {
        if (isRecordingPlaying == value)
            return;

        isRecordingPlaying = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordingPlaying)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPlaybackActionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPlaybackHelpText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPlaybackToolTip)));
    }

    internal void SetStartsActivityDay(bool value)
    {
        if (startsActivityDay == value)
            return;
        startsActivityDay = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartsActivityDay)));
    }

    public bool ObserveStream(uint streamId)
    {
        if (streamId == 0 || streamIds.Contains(streamId))
            return false;

        if (streamIds.Count >= ReceiveCallEpisodeTracker.MaximumStreamsPerEpisode)
            streamIds.RemoveAt(1);
        streamIds.Add(streamId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamIds)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamFragmentCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamText)));
        return true;
    }

    public static CallHistoryEntry CreateRecordingOnly(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        bool isTx = metadata.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase);
        FneTrafficProtocol protocol = EncryptionPresentation.ParseProtocol(metadata.Protocol);
        EncryptionSnapshot encryption = EncryptionSnapshotSchemaAdapter.FromMetadata(metadata);
        string caller = string.IsNullOrWhiteSpace(metadata.SubscriberAlias)
            ? metadata.SubscriberId?.ToString() ?? "Unknown"
            : metadata.SubscriberAlias.Trim();
        var entry = new CallHistoryEntry(
            metadata.UtcStartTime,
            metadata.SystemName,
            metadata.ChannelName,
            metadata.SubscriberId ?? 0,
            metadata.TalkgroupId ?? 0,
            protocol,
            metadata.StreamId ?? 0,
            caller,
            encryption.IsSecure,
            isConsoleTransmission: isTx,
            isRecordingOnly: true,
            receiveEpisodeId: metadata.ReceiveEpisodeId,
            encryptionKnown: encryption.IsKnown);
        entry.UpdateEncryption(encryption);
        entry.endTimestamp = metadata.UtcEndTime >= metadata.UtcStartTime
            ? metadata.UtcEndTime
            : metadata.UtcStartTime.AddMilliseconds(Math.Max(0, metadata.DurationMs));
        foreach (uint streamId in metadata.StreamIds ?? [])
            entry.ObserveStream(streamId);
        entry.SetRecording(metadata);
        return entry;
    }

    public static CallHistoryEntry CreateEvent(
        DateTimeOffset timestamp,
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        string normalizedSource = source.Trim();
        return new CallHistoryEntry(
            timestamp,
            normalizedSource,
            normalizedSource,
            0,
            0,
            FneTrafficProtocol.Dmr,
            0,
            callerText: message,
            isEvent: true,
            eventSource: normalizedSource,
            eventMessage: message,
            eventRidText: ridText,
            eventTgidText: tgidText);
    }

    public void Complete(DateTimeOffset timestamp)
    {
        if (endTimestamp is not null)
            return;
        endTimestamp = timestamp < Timestamp ? Timestamp : timestamp;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndTimestamp)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
    }

    public bool UpdateEncryption(bool value)
        => UpdateEncryption(value, null, null);

    public bool UpdateEncryption(bool value, byte? algorithmId, ushort? keyId)
        => UpdateEncryption(EncryptionSnapshot.FromStored(
            value
                ? CallRecordingEncryptionState.Secure
                : CallRecordingEncryptionState.Clear,
            algorithmId,
            keyId));

    internal bool UpdateEncryption(EncryptionSnapshot value)
    {
        if (encryption.HasSameMetadata(value) && encryption.IsKnown == value.IsKnown)
            return false;

        encryption = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Encrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionKnown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAlgorithmId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionKeyId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionText)));
        return true;
    }
}

// Bounded newest-first call history for the Avalonia shell.
public sealed class CallHistoryStore
{
    public const int DefaultMaxEntries = ConsoleCallHistory.DefaultMaximumEntries;

    private readonly int maxEntries;
    private readonly object sync = new();
    private readonly ResettableObservableCollection<CallHistoryEntry> entries = [];
    private readonly ConsoleCallHistory applicationHistory;

    public CallHistoryStore(int maxEntries = DefaultMaxEntries)
        : this(new ConsoleCallHistory(maxEntries)) { }

    internal CallHistoryStore(ConsoleCallHistory history)
    {
        applicationHistory = history ?? throw new ArgumentNullException(nameof(history));
        maxEntries = history.MaximumEntries;
    }

    public ObservableCollection<CallHistoryEntry> Entries => entries;
    internal ConsoleCallHistory Runtime => applicationHistory;
    internal IReadOnlyList<ConsoleCallHistoryRecord> ApplicationHistory => applicationHistory.Snapshot;

    internal RecordingCatalogReconciliationMetrics LastRecordingCatalogReconciliation { get; private set; }
        = new(0, 0, 0, 0, 0);

    public bool HasActiveReceiveCall(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        string channelName,
        uint destinationId,
        long? receiveEpisodeId = null)
    {
        lock (sync)
        {
            return applicationHistory.FindActiveReceive(
                systemName,
                ToRadioProtocol(protocol),
                streamId,
                channelName,
                destinationId,
                receiveEpisodeId) is not null;
        }
    }

    public bool ObserveReceiveStream(
        string systemName,
        FneTrafficProtocol protocol,
        uint primaryStreamId,
        uint physicalStreamId,
        string channelName,
        uint destinationId,
        long? receiveEpisodeId = null)
    {
        lock (sync)
        {
            CallHistoryEntry? entry = FindActiveReceiveCall(
                systemName,
                protocol,
                primaryStreamId,
                channelName,
                destinationId,
                receiveEpisodeId);
            if (entry?.ObserveStream(physicalStreamId) != true)
                return false;
            applicationHistory.ObserveStream(entry.Id, physicalStreamId);
            return true;
        }
    }

    public void Add(CallHistoryEntry entry) => Add(entry, publishToRuntime: true);

    private void Add(CallHistoryEntry entry, bool publishToRuntime)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (sync)
        {
            if (!entry.IsEvent && !entry.IsRecordingOnly)
            {
                CallHistoryEntry? archived = Entries.FirstOrDefault(candidate =>
                    candidate.IsRecordingOnly &&
                    candidate.Recording is not null &&
                    RecordingMatchesCall(candidate.Recording, entry));
                if (archived is not null)
                {
                    SetRecording(entry, archived.Recording);
                    Entries.Remove(archived);
                }
            }
            if (publishToRuntime && !entry.IsRecordingOnly)
                applicationHistory.Add(ProjectApplicationHistory(entry));
            InsertNewestFirst(entry);
            TrimSessionEntries();
        }
    }

    internal void ProjectRuntimeRecord(ConsoleCallHistoryRecord record)
    {
        lock (sync)
        {
            CallHistoryEntry? entry = entries.FirstOrDefault(candidate => candidate.Id == record.Id);
            if (entry is null)
            {
                entry = new CallHistoryEntry(record.StartedAt, record.SystemName, record.ChannelName,
                    record.SourceId, record.DestinationId, FneReceiveWorkQueueAdapter.ToFneProtocol(record.Protocol),
                    record.PrimaryStreamId, record.Caller, record.Encryption.IsSecure,
                    isEvent: record.Direction == ConsoleCallDirection.Event,
                    isConsoleTransmission: record.Direction == ConsoleCallDirection.Transmit,
                    eventSource: record.EventSource, eventMessage: record.EventMessage,
                    eventRidText: record.EventRid, eventTgidText: record.EventTalkgroup,
                    receiveEpisodeId: record.ReceiveEpisodeId, encryptionKnown: record.Encryption.IsKnown,
                    callId: record.Id, channelId: record.ChannelId);
                Add(entry, publishToRuntime: false);
            }
            foreach (uint stream in record.StreamIds) entry.ObserveStream(stream);
            entry.UpdateEncryption(record.Encryption.IsKnown
                ? EncryptionSnapshot.FromStored(record.Encryption.IsSecure
                    ? CallRecordingEncryptionState.Secure : CallRecordingEncryptionState.Clear,
                    record.Encryption.AlgorithmId, record.Encryption.KeyId)
                : EncryptionSnapshot.Unknown);
            if (record.EndedAt is { } ended) entry.Complete(ended);
        }
    }

    private void SetRecording(CallHistoryEntry entry, CallRecordingMetadata? recording)
    {
        applicationHistory.SetRecordingAttached(entry.Id, recording is not null);
        entry.SetRecording(recording);
    }

    public CallHistoryEntry AddOrAttachRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (sync)
        {
            CallHistoryEntry? byRecordingId = Entries.FirstOrDefault(entry => RecordingEquals(entry.Recording, metadata));
            if (byRecordingId is not null)
            {
                SetRecording(byRecordingId, metadata);
                return byRecordingId;
            }

            CallHistoryEntry? call = FindBestRecordingCall(
                Entries.Where(entry => !entry.IsEvent && !entry.IsRecordingOnly),
                metadata);
            if (call is not null)
            {
                SetRecording(call, metadata);
                return call;
            }

            CallHistoryEntry archived = CallHistoryEntry.CreateRecordingOnly(metadata);
            InsertNewestFirst(archived);
            return archived;
        }
    }

    public void RemoveRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (sync)
        {
            CallHistoryEntry? entry = Entries.FirstOrDefault(candidate => RecordingEquals(candidate.Recording, metadata));
            if (entry is null)
                return;
            if (entry.IsRecordingOnly)
                Entries.Remove(entry);
            else
                SetRecording(entry, null);
        }
    }

    public void ReplaceRecordingCatalog(IEnumerable<CallRecordingMetadata> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        lock (sync)
        {
            CallRecordingMetadata[] desired = recordings.ToArray();
            if (!IsNewestFirst(desired))
            {
                Array.Sort(desired, static (left, right) =>
                {
                    int timestamp = right.UtcStartTime.CompareTo(left.UtcStartTime);
                    return timestamp != 0
                        ? timestamp
                        : StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
                });
            }

            long existingEntryVisits = entries.Count * 3L;
            var desiredKeys = new HashSet<string>(
                desired.Select(RecordingKey),
                StringComparer.Ordinal);
            CallHistoryEntry[] sessionEntries = entries
                .Where(entry => !entry.IsRecordingOnly)
                .ToArray();
            Dictionary<string, CallHistoryEntry> existingByRecording = entries
                .Where(entry => entry.Recording is not null)
                .GroupBy(entry => RecordingKey(entry.Recording!), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var calls = new RecordingCallIndex<CallHistoryEntry>(
                sessionEntries.Where(entry => !entry.IsEvent && !entry.IsRecordingOnly), DescribeRecordingCall);

            foreach (CallHistoryEntry entry in sessionEntries)
            {
                if (entry.Recording is CallRecordingMetadata recording &&
                    !desiredKeys.Contains(RecordingKey(recording)))
                {
                    SetRecording(entry, null);
                }
            }

            var catalogRows = new List<CallHistoryEntry>(desired.Length);
            var processedKeys = new HashSet<string>(StringComparer.Ordinal);
            long desiredVisits = 0;
            long keyLookups = 0;
            long identityCandidateVisits = 0;
            foreach (CallRecordingMetadata metadata in desired)
            {
                desiredVisits++;
                string recordingKey = RecordingKey(metadata);
                if (!processedKeys.Add(recordingKey))
                    continue;

                keyLookups++;
                if (existingByRecording.TryGetValue(recordingKey, out CallHistoryEntry? existing))
                {
                    SetRecording(existing, metadata);
                    if (existing.IsRecordingOnly)
                        catalogRows.Add(existing);
                    continue;
                }

                keyLookups++;
                CallHistoryEntry? call = calls.FindBest(metadata.ToCallIdentity(), out int candidateVisits);
                identityCandidateVisits += candidateVisits;
                if (call is not null)
                    SetRecording(call, metadata);
                else
                    catalogRows.Add(CallHistoryEntry.CreateRecordingOnly(metadata));
            }

            List<CallHistoryEntry> merged = MergeNewestFirst(sessionEntries, catalogRows).ToList();
            entries.ReplaceAll(merged);
            LastRecordingCatalogReconciliation = new RecordingCatalogReconciliationMetrics(
                existingEntryVisits,
                desiredVisits,
                keyLookups,
                identityCandidateVisits,
                merged.Count);
        }
    }

    public void RemoveRecordingsByKey(IEnumerable<string> recordingKeys)
    {
        ArgumentNullException.ThrowIfNull(recordingKeys);
        lock (sync)
        {
            var keys = new HashSet<string>(recordingKeys, StringComparer.Ordinal);
            if (keys.Count == 0)
                return;

            foreach (CallHistoryEntry entry in Entries
                         .Where(entry => entry.Recording is not null &&
                             keys.Contains(RecordingKey(entry.Recording!)))
                         .ToArray())
            {
                if (entry.IsRecordingOnly)
                    Entries.Remove(entry);
                else
                    SetRecording(entry, null);
            }
        }
    }

    public void AddOrAttachRecordings(IEnumerable<CallRecordingMetadata> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        lock (sync)
        {
            CallRecordingMetadata[] batch = recordings.ToArray();
            if (batch.Length == 0)
                return;

            Dictionary<string, CallHistoryEntry> byRecording = Entries
                .Where(entry => entry.Recording is not null)
                .GroupBy(entry => RecordingKey(entry.Recording!), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var calls = new RecordingCallIndex<CallHistoryEntry>(
                Entries.Where(entry => !entry.IsEvent && !entry.IsRecordingOnly), DescribeRecordingCall);

            foreach (CallRecordingMetadata metadata in batch)
            {
                string recordingKey = RecordingKey(metadata);
                if (byRecording.TryGetValue(recordingKey, out CallHistoryEntry? existing))
                {
                    SetRecording(existing, metadata);
                    continue;
                }

                CallHistoryEntry? call = calls.FindBest(metadata.ToCallIdentity());
                if (call is null)
                {
                    call = CallHistoryEntry.CreateRecordingOnly(metadata);
                    InsertNewestFirst(call);
                }
                else
                {
                    SetRecording(call, metadata);
                }
                byRecording[recordingKey] = call;
            }
        }
    }

    private void InsertNewestFirst(CallHistoryEntry entry)
    {
        int index = 0;
        while (index < Entries.Count && Entries[index].Timestamp > entry.Timestamp)
            index++;
        Entries.Insert(index, entry);
    }

    private static IEnumerable<CallHistoryEntry> MergeNewestFirst(
        IReadOnlyList<CallHistoryEntry> sessionEntries,
        IReadOnlyList<CallHistoryEntry> catalogRows)
    {
        int sessionIndex = 0;
        int catalogIndex = 0;
        while (sessionIndex < sessionEntries.Count && catalogIndex < catalogRows.Count)
        {
            if (catalogRows[catalogIndex].Timestamp >= sessionEntries[sessionIndex].Timestamp)
                yield return catalogRows[catalogIndex++];
            else
                yield return sessionEntries[sessionIndex++];
        }
        while (sessionIndex < sessionEntries.Count)
            yield return sessionEntries[sessionIndex++];
        while (catalogIndex < catalogRows.Count)
            yield return catalogRows[catalogIndex++];
    }

    private static bool IsNewestFirst(IReadOnlyList<CallRecordingMetadata> recordings)
    {
        for (int index = 1; index < recordings.Count; index++)
        {
            if (recordings[index - 1].UtcStartTime < recordings[index].UtcStartTime)
                return false;
            if (recordings[index - 1].UtcStartTime == recordings[index].UtcStartTime &&
                StringComparer.OrdinalIgnoreCase.Compare(
                    recordings[index - 1].FileName,
                    recordings[index].FileName) > 0)
            {
                return false;
            }
        }
        return true;
    }

    private void TrimSessionEntries()
    {
        while (Entries.Count(entry => !entry.IsRecordingOnly) > maxEntries)
        {
            CallHistoryEntry? oldest = Entries.LastOrDefault(entry => !entry.IsRecordingOnly);
            if (oldest is null)
                break;
            Entries.Remove(oldest);
            if (oldest.Recording is CallRecordingMetadata recording)
                InsertNewestFirst(CallHistoryEntry.CreateRecordingOnly(recording));
        }
    }

    private static bool RecordingEquals(CallRecordingMetadata? left, CallRecordingMetadata right)
        => RecordingDisplayIdentity.Matches(left, right);

    private static string RecordingKey(CallRecordingMetadata recording)
        => !string.IsNullOrWhiteSpace(recording.RecordingId)
            ? $"id:{recording.RecordingId.ToUpperInvariant()}"
            : $"path:{NormalizeRecordingPath(recording.FilePath)}";

    private static string NormalizeRecordingPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static RecordingCallIdentity DescribeRecordingCall(CallHistoryEntry call)
        => new(call.Timestamp, call.SystemName, call.ChannelName, call.DirectionText,
            call.ProtocolText, call.SourceId, call.DestinationId, call.StreamId, call.ReceiveEpisodeId, call.EndTimestamp);

    private static bool RecordingMatchesCall(CallRecordingMetadata recording, CallHistoryEntry call)
        => RecordingCallMatcher.Matches(recording.ToCallIdentity(), DescribeRecordingCall(call));

    private static CallHistoryEntry? FindBestRecordingCall(
        IEnumerable<CallHistoryEntry> candidates,
        CallRecordingMetadata recording)
        => candidates
            .Where(candidate => RecordingMatchesCall(recording, candidate))
            .OrderBy(candidate => Math.Abs((candidate.Timestamp - recording.UtcStartTime).Ticks))
            .FirstOrDefault();

    public bool Complete(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        DateTimeOffset timestamp,
        string? channelName = null,
        uint? destinationId = null,
        long? receiveEpisodeId = null)
    {
        ConsoleCallCompletion? completed = CompleteRuntime(systemName, protocol, streamId,
            timestamp, channelName, destinationId, receiveEpisodeId);
        if (completed is null) return false;
        ProjectCompletion(completed);
        return true;
    }

    // Runtime completion reads only application state; observable row changes
    // stay in ProjectCompletion.
    internal ConsoleCallCompletion? CompleteRuntime(
        string systemName, FneTrafficProtocol protocol, uint streamId, DateTimeOffset timestamp,
        string? channelName = null, uint? destinationId = null, long? receiveEpisodeId = null)
    {
        lock (sync)
            return applicationHistory.CompleteReceive(
                systemName, ToRadioProtocol(protocol), streamId, timestamp,
                channelName, destinationId, receiveEpisodeId);
    }

    internal void ProjectCompletion(ConsoleCallCompletion completion)
    {
        lock (sync)
        {
            // A delayed projection must respect later recording attachment or
            // history clearing, rather than recreating its old completion snapshot.
            if (applicationHistory.Find(completion.Record.Id) is { } current)
                ProjectRuntimeRecord(current);
            else if (Entries.FirstOrDefault(entry => entry.Id == completion.Record.Id) is { } removed)
                Entries.Remove(removed);
        }
    }

    public bool UpdateEncryption(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        bool encrypted)
        => UpdateEncryption(systemName, protocol, streamId, encrypted, null, null);

    public bool UpdateEncryption(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        bool encrypted,
        byte? algorithmId,
        ushort? keyId,
        string? channelName = null,
        uint? destinationId = null,
        long? receiveEpisodeId = null)
        => UpdateEncryption(
            systemName,
            protocol,
            streamId,
            EncryptionSnapshot.FromStored(
                encrypted
                    ? CallRecordingEncryptionState.Secure
                    : CallRecordingEncryptionState.Clear,
                algorithmId,
                keyId),
            channelName,
            destinationId,
            receiveEpisodeId);

    internal bool UpdateEncryption(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        EncryptionSnapshot encryption,
        string? channelName = null,
        uint? destinationId = null,
        long? receiveEpisodeId = null)
    {
        lock (sync)
        {
            CallHistoryEntry? entry = FindActiveReceiveCall(
                systemName,
                protocol,
                streamId,
                channelName,
                destinationId,
                receiveEpisodeId);
            if (entry?.UpdateEncryption(encryption) != true)
                return false;
            applicationHistory.UpdateEncryption(entry.Id, ToApplicationEncryption(encryption));
            return true;
        }
    }

    private CallHistoryEntry? FindActiveReceiveCall(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        string? channelName,
        uint? destinationId,
        long? receiveEpisodeId = null)
    {
        CallId? id = applicationHistory.FindActiveReceive(
            systemName,
            ToRadioProtocol(protocol),
            streamId,
            channelName,
            destinationId,
            receiveEpisodeId);
        return id is null
            ? null
            : Entries.FirstOrDefault(candidate => candidate.Id == id.Value);
    }

    public void AddEvent(
        DateTimeOffset timestamp,
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
        => Add(CallHistoryEntry.CreateEvent(timestamp, source, message, ridText, tgidText));

    internal void AddConsoleTransmission(DateTimeOffset timestamp, TransmitTarget target, uint streamId)
    {
        lock (sync)
            ProjectRuntimeRecord(applicationHistory.BeginTransmit(timestamp, target, streamId));
    }

    public void AddConsoleTransmission(
        DateTimeOffset timestamp,
        string systemName,
        string channelName,
        uint sourceId,
        uint destinationId,
        FneTrafficProtocol protocol,
        uint streamId,
        string? callerText = null,
        bool encrypted = false,
        byte? encryptionAlgorithmId = null,
        ushort? encryptionKeyId = null,
        ChannelId? channelId = null)
    {
        lock (sync)
        {
            ConsoleCallHistoryRecord record = applicationHistory.BeginTransmit(
                timestamp, systemName, channelName, sourceId, destinationId,
                ToRadioProtocol(protocol), streamId, callerText, encrypted,
                encryptionAlgorithmId, encryptionKeyId, channelId);
            ProjectRuntimeRecord(record);
        }
    }

    public bool CompleteConsoleTransmission(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        DateTimeOffset timestamp,
        string? channelName = null,
        uint? destinationId = null)
    {
        lock (sync)
        {
            CallId? id = applicationHistory.FindActiveTransmit(
                systemName,
                ToRadioProtocol(protocol),
                streamId,
                channelName,
                destinationId);
            CallHistoryEntry? entry = id is null
                ? null
                : Entries.FirstOrDefault(candidate => candidate.Id == id.Value);
            if (entry is null)
                return false;
            applicationHistory.Complete(entry.Id, timestamp);
            entry.Complete(timestamp);
            return true;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            CallRecordingMetadata[] attachedRecordings = Entries
                .Where(entry => !entry.IsRecordingOnly && entry.Recording is not null)
                .Select(entry => entry.Recording!)
                .ToArray();
            foreach (CallHistoryEntry entry in Entries.Where(entry => !entry.IsRecordingOnly).ToArray())
                Entries.Remove(entry);
            applicationHistory.Clear();
            foreach (CallRecordingMetadata recording in attachedRecordings)
                AddOrAttachRecording(recording);
        }
    }

    private static ConsoleCallHistoryRecord ProjectApplicationHistory(CallHistoryEntry entry)
        => new(
            entry.Id,
            entry.Timestamp,
            entry.EndTimestamp,
            SystemId.FromName(entry.SystemName),
            entry.SystemName,
            entry.ChannelId,
            entry.ChannelName,
            ToRadioProtocol(entry.Protocol),
            entry.SourceId,
            entry.DestinationId,
            entry.StreamId,
            entry.StreamIds.ToArray(),
            entry.ReceiveEpisodeId,
            entry.CallerText,
            entry.IsEvent
                ? ConsoleCallDirection.Event
                : entry.IsConsoleTransmission
                    ? ConsoleCallDirection.Transmit
                    : ConsoleCallDirection.Receive,
            new RecordingEncryptionDescriptor(
                entry.EncryptionKnown,
                entry.Encrypted,
                entry.EncryptionAlgorithmId,
                entry.EncryptionKeyId),
            entry.EventSource,
            entry.EventMessage,
            entry.EventRidText,
            entry.EventTgidText)
        { HasRecording = entry.HasRecording };

    private static RecordingEncryptionDescriptor ToApplicationEncryption(EncryptionSnapshot encryption)
        => new(
            encryption.IsKnown,
            encryption.IsSecure,
            encryption.AlgorithmId,
            encryption.KeyId);

    private static DvmConsole.Core.Runtime.RadioMediaProtocol ToRadioProtocol(
        FneTrafficProtocol protocol)
        => protocol switch
        {
            FneTrafficProtocol.Dmr => DvmConsole.Core.Runtime.RadioMediaProtocol.Dmr,
            FneTrafficProtocol.P25 => DvmConsole.Core.Runtime.RadioMediaProtocol.P25,
            FneTrafficProtocol.Nxdn => DvmConsole.Core.Runtime.RadioMediaProtocol.Nxdn,
            FneTrafficProtocol.Analog => DvmConsole.Core.Runtime.RadioMediaProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
}
