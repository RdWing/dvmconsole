using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// One inbound voice stream recorded by the dispatch shell.
public sealed class CallHistoryEntry : INotifyPropertyChanged
{
    private DateTimeOffset? endTimestamp;
    private bool encrypted;
    private byte? encryptionAlgorithmId;
    private ushort? encryptionKeyId;
    private readonly bool isEvent;
    private readonly bool isConsoleTransmission;
    private readonly bool isRecordingOnly;
    private readonly string eventSource;
    private readonly string eventMessage;
    private readonly string eventRidText;
    private readonly string eventTgidText;
    private CallRecordingMetadata? recording;

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
        string? eventTgidText = null)
    {
        Timestamp = timestamp;
        SystemName = systemName;
        ChannelName = channelName;
        SourceId = sourceId;
        DestinationId = destinationId;
        Protocol = protocol;
        StreamId = streamId;
        CallerText = string.IsNullOrWhiteSpace(callerText) ? sourceId.ToString() : callerText.Trim();
        this.encrypted = encrypted;
        this.isEvent = isEvent;
        this.isConsoleTransmission = isConsoleTransmission;
        this.isRecordingOnly = isRecordingOnly;
        this.eventSource = eventSource?.Trim() ?? string.Empty;
        this.eventMessage = eventMessage?.Trim() ?? string.Empty;
        this.eventRidText = eventRidText?.Trim() ?? string.Empty;
        this.eventTgidText = eventTgidText?.Trim() ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset? EndTimestamp => endTimestamp;
    public string SystemName { get; }
    public string ChannelName { get; }
    public uint SourceId { get; }
    public uint DestinationId { get; }
    public FneTrafficProtocol Protocol { get; }
    public uint StreamId { get; }
    public string CallerText { get; }
    public bool IsEvent => isEvent;
    public bool IsConsoleTransmission => isConsoleTransmission;
    public bool IsRecordingOnly => isRecordingOnly;
    public string DirectionText => IsEvent ? "EVENT" : IsConsoleTransmission ? "TX" : "RX";
    public string EventSource => eventSource;
    public string EventMessage => eventMessage;
    public string EventRidText => eventRidText;
    public string EventTgidText => eventTgidText;
    public bool Encrypted => !IsEvent && encrypted;
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string DateText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd");
    public string ProtocolText => IsEvent ? "EVENT" : Protocol.ToString().ToUpperInvariant();
    public string DisplayChannelText => IsEvent ? EventSource : ChannelName;
    public string DisplaySourceText => IsEvent ? EventRidText : SourceId.ToString();
    public string DisplayDestinationText => IsEvent ? EventTgidText : DestinationId.ToString();
    public string RouteText => IsEvent ? EventMessage : $"{CallerText} → TG {DestinationId}";
    public string StreamText => IsEvent ? "Event" : $"{ProtocolText} · stream {StreamId}";
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
    public byte? EncryptionAlgorithmId => encryptionAlgorithmId;
    public ushort? EncryptionKeyId => encryptionKeyId;
    public string EncryptionText => IsEvent
        ? "—"
        : EncryptionPresentation.StatusText(Encrypted, Protocol, encryptionAlgorithmId);

    public bool HasRecording => recording is not null;
    public bool HasPlayableRecording => recording?.IsPlayable == true;
    public CallRecordingMetadata? Recording => recording;
    public string RecordingFileName => recording?.FileName ?? string.Empty;
    public string RecordingDetailsText => recording?.TechnicalDetailsText ?? string.Empty;
    public string RecordingPath => recording?.FilePath ?? string.Empty;

    public void SetRecording(CallRecordingMetadata? value)
    {
        if (ReferenceEquals(recording, value))
            return;
        recording = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlayableRecording)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingFileName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingDetailsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingPath)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
    }

    public static CallHistoryEntry CreateRecordingOnly(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        bool isTx = metadata.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase);
        FneTrafficProtocol protocol = metadata.Protocol.Trim().ToUpperInvariant() switch
        {
            "P25" => FneTrafficProtocol.P25,
            "NXDN" => FneTrafficProtocol.Nxdn,
            "ANALOG" => FneTrafficProtocol.Analog,
            _ => FneTrafficProtocol.Dmr
        };
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
            metadata.IsEncrypted,
            isConsoleTransmission: isTx,
            isRecordingOnly: true);
        entry.endTimestamp = metadata.UtcEndTime >= metadata.UtcStartTime
            ? metadata.UtcEndTime
            : metadata.UtcStartTime.AddMilliseconds(Math.Max(0, metadata.DurationMs));
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

    public static CallHistoryEntry CreateConsoleTransmission(
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
        ushort? encryptionKeyId = null)
    {
        var entry = new CallHistoryEntry(
            timestamp,
            systemName,
            channelName,
            sourceId,
            destinationId,
            protocol,
            streamId,
            callerText,
            encrypted,
            isConsoleTransmission: true);
        if (encrypted)
            entry.UpdateEncryption(true, encryptionAlgorithmId, encryptionKeyId);
        return entry;
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
    {
        if (encrypted == value &&
            encryptionAlgorithmId == algorithmId &&
            encryptionKeyId == keyId)
            return false;

        encrypted = value;
        encryptionAlgorithmId = value ? algorithmId : null;
        encryptionKeyId = value ? keyId : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Encrypted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionAlgorithmId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionKeyId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EncryptionText)));
        return true;
    }
}

// Bounded newest-first call history for the Avalonia shell.
public sealed class CallHistoryStore
{
    public const int DefaultMaxEntries = 100;
    private static readonly TimeSpan MinimumVisibleCallDuration = TimeSpan.FromMilliseconds(50);

    private readonly int maxEntries;

    public CallHistoryStore(int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        this.maxEntries = maxEntries;
    }

    public ObservableCollection<CallHistoryEntry> Entries { get; } = [];

    public bool HasActiveReceiveCall(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        string channelName,
        uint destinationId)
        => Entries.Any(candidate =>
            candidate.IsActive &&
            !candidate.IsConsoleTransmission &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            candidate.DestinationId == destinationId &&
            candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase) &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));

    public void Add(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsEvent && !entry.IsRecordingOnly)
        {
            CallHistoryEntry? archived = Entries.FirstOrDefault(candidate =>
                candidate.IsRecordingOnly &&
                candidate.Recording is not null &&
                RecordingMatchesCall(candidate.Recording, entry));
            if (archived is not null)
            {
                entry.SetRecording(archived.Recording);
                Entries.Remove(archived);
            }
        }
        InsertNewestFirst(entry);
        TrimSessionEntries();
    }

    public CallHistoryEntry AddOrAttachRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        CallHistoryEntry? byRecordingId = Entries.FirstOrDefault(entry => RecordingEquals(entry.Recording, metadata));
        if (byRecordingId is not null)
        {
            byRecordingId.SetRecording(metadata);
            return byRecordingId;
        }

        string direction = metadata.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX";
        CallHistoryEntry? call = Entries.FirstOrDefault(entry =>
            !entry.IsEvent &&
            !entry.IsRecordingOnly &&
            entry.StreamId == metadata.StreamId &&
            entry.DirectionText == direction &&
            entry.ChannelName.Equals(metadata.ChannelName, StringComparison.OrdinalIgnoreCase) &&
            entry.DestinationId == metadata.TalkgroupId &&
            entry.SystemName.Equals(metadata.SystemName, StringComparison.OrdinalIgnoreCase) &&
            entry.ProtocolText.Equals(metadata.Protocol, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((entry.Timestamp - metadata.UtcStartTime).TotalSeconds) <= 5);
        if (call is not null)
        {
            call.SetRecording(metadata);
            return call;
        }

        CallHistoryEntry archived = CallHistoryEntry.CreateRecordingOnly(metadata);
        InsertNewestFirst(archived);
        return archived;
    }

    public void RemoveRecording(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate => RecordingEquals(candidate.Recording, metadata));
        if (entry is null)
            return;
        if (entry.IsRecordingOnly)
            Entries.Remove(entry);
        else
            entry.SetRecording(null);
    }

    public void ReplaceRecordingCatalog(IEnumerable<CallRecordingMetadata> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        CallRecordingMetadata[] desired = recordings.ToArray();
        var desiredIds = new HashSet<string>(
            desired.Select(RecordingKey),
            StringComparer.OrdinalIgnoreCase);
        string[] removedKeys = Entries
            .Where(entry => entry.Recording is not null)
            .Select(entry => RecordingKey(entry.Recording!))
            .Where(key => !desiredIds.Contains(key))
            .ToArray();
        RemoveRecordingsByKey(removedKeys);
        AddOrAttachRecordings(desired);
    }

    public void RemoveRecordingsByKey(IEnumerable<string> recordingKeys)
    {
        ArgumentNullException.ThrowIfNull(recordingKeys);
        var keys = new HashSet<string>(recordingKeys, StringComparer.OrdinalIgnoreCase);
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
                entry.SetRecording(null);
        }
    }

    public void AddOrAttachRecordings(IEnumerable<CallRecordingMetadata> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        CallRecordingMetadata[] batch = recordings.ToArray();
        if (batch.Length == 0)
            return;

        Dictionary<string, CallHistoryEntry> byRecording = Entries
            .Where(entry => entry.Recording is not null)
            .GroupBy(entry => RecordingKey(entry.Recording!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CallHistoryEntry[]> callsByIdentity = Entries
            .Where(entry => !entry.IsEvent && !entry.IsRecordingOnly)
            .GroupBy(CallIdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (CallRecordingMetadata metadata in batch)
        {
            string recordingKey = RecordingKey(metadata);
            if (byRecording.TryGetValue(recordingKey, out CallHistoryEntry? existing))
            {
                existing.SetRecording(metadata);
                continue;
            }

            CallHistoryEntry? call = callsByIdentity
                .GetValueOrDefault(RecordingIdentityKey(metadata))?
                .FirstOrDefault(candidate => Math.Abs((candidate.Timestamp - metadata.UtcStartTime).TotalSeconds) <= 5);
            if (call is null)
            {
                call = CallHistoryEntry.CreateRecordingOnly(metadata);
                InsertNewestFirst(call);
            }
            else
            {
                call.SetRecording(metadata);
            }
            byRecording[recordingKey] = call;
        }
    }

    private void InsertNewestFirst(CallHistoryEntry entry)
    {
        int index = 0;
        while (index < Entries.Count && Entries[index].Timestamp > entry.Timestamp)
            index++;
        Entries.Insert(index, entry);
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
    {
        if (left is null)
            return false;
        if (!string.IsNullOrWhiteSpace(left.RecordingId) && !string.IsNullOrWhiteSpace(right.RecordingId))
            return left.RecordingId.Equals(right.RecordingId, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(left.FilePath) &&
            left.FilePath.Equals(right.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string RecordingKey(CallRecordingMetadata recording)
        => !string.IsNullOrWhiteSpace(recording.RecordingId)
            ? recording.RecordingId
            : recording.FilePath;

    private static string CallIdentityKey(CallHistoryEntry call)
        => string.Join('\u001f',
            call.SystemName,
            call.ProtocolText,
            call.DirectionText,
            call.ChannelName,
            call.DestinationId.ToString(CultureInfo.InvariantCulture),
            call.StreamId.ToString(CultureInfo.InvariantCulture));

    private static string RecordingIdentityKey(CallRecordingMetadata recording)
        => string.Join('\u001f',
            recording.SystemName,
            recording.Protocol.Trim().ToUpperInvariant(),
            recording.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX",
            recording.ChannelName,
            recording.TalkgroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            recording.StreamId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static bool RecordingMatchesCall(CallRecordingMetadata recording, CallHistoryEntry call)
    {
        string direction = recording.Direction.Equals("TX", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX";
        return call.StreamId == recording.StreamId &&
            call.DirectionText == direction &&
            call.ChannelName.Equals(recording.ChannelName, StringComparison.OrdinalIgnoreCase) &&
            call.DestinationId == recording.TalkgroupId &&
            call.SystemName.Equals(recording.SystemName, StringComparison.OrdinalIgnoreCase) &&
            call.ProtocolText.Equals(recording.Protocol, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((call.Timestamp - recording.UtcStartTime).TotalSeconds) <= 5;
    }

    public bool Complete(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        DateTimeOffset timestamp,
        string? channelName = null,
        uint? destinationId = null)
    {
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.IsActive &&
            !candidate.IsConsoleTransmission &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            (channelName is null || candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase)) &&
            (destinationId is null || candidate.DestinationId == destinationId) &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return false;
        entry.Complete(timestamp);
        // Busy FNEs can announce and immediately replace a stream before one
        // complete voice frame arrives. Do not leave those sub-frame shells as
        // duplicate-looking 0.0s calls. If TAR later finalizes playable audio,
        // AddOrAttachRecording restores it as a recording-backed catalog row.
        if (!entry.HasRecording && entry.Duration < MinimumVisibleCallDuration)
            Entries.Remove(entry);
        return true;
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
        uint? destinationId = null)
    {
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.IsActive &&
            !candidate.IsConsoleTransmission &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            (channelName is null || candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase)) &&
            (destinationId is null || candidate.DestinationId == destinationId) &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));
        return entry?.UpdateEncryption(encrypted, algorithmId, keyId) == true;
    }

    public void AddEvent(
        DateTimeOffset timestamp,
        string source,
        string message,
        string? ridText = null,
        string? tgidText = null)
        => Add(CallHistoryEntry.CreateEvent(timestamp, source, message, ridText, tgidText));

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
        ushort? encryptionKeyId = null)
        => Add(CallHistoryEntry.CreateConsoleTransmission(
            timestamp,
            systemName,
            channelName,
            sourceId,
            destinationId,
            protocol,
            streamId,
            callerText,
            encrypted,
            encryptionAlgorithmId,
            encryptionKeyId));

    public bool CompleteConsoleTransmission(
        string systemName,
        FneTrafficProtocol protocol,
        uint streamId,
        DateTimeOffset timestamp,
        string? channelName = null,
        uint? destinationId = null)
    {
        CallHistoryEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.IsActive &&
            candidate.IsConsoleTransmission &&
            candidate.StreamId == streamId &&
            candidate.Protocol == protocol &&
            (channelName is null || candidate.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase)) &&
            (destinationId is null || candidate.DestinationId == destinationId) &&
            candidate.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return false;
        entry.Complete(timestamp);
        return true;
    }

    public void Clear()
    {
        CallRecordingMetadata[] attachedRecordings = Entries
            .Where(entry => !entry.IsRecordingOnly && entry.Recording is not null)
            .Select(entry => entry.Recording!)
            .ToArray();
        foreach (CallHistoryEntry entry in Entries.Where(entry => !entry.IsRecordingOnly).ToArray())
            Entries.Remove(entry);
        foreach (CallRecordingMetadata recording in attachedRecordings)
            AddOrAttachRecording(recording);
    }
}
