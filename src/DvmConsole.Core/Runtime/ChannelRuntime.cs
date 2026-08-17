using System.ComponentModel;
using System.Runtime.CompilerServices;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Core.Runtime;

public enum ChannelRuntimeState
{
    Idle,
    Receiving,
    Transmitting,
    Faulted
}

// Immutable channel identity used by services and views without depending on
// a WPF/Avalonia control instance.
public sealed record ChannelRuntimeDefinition
{
    public ChannelRuntimeDefinition(
        string name,
        string systemName,
        string mode,
        uint destinationId,
        byte slot,
        bool rxOnly = false,
        string? encryptionAlgorithm = null,
        string? encryptionKeyId = null,
        bool selectableEncryption = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A channel name is required.", nameof(name)) : name.Trim();
        SystemName = string.IsNullOrWhiteSpace(systemName) ? throw new ArgumentException("A channel system is required.", nameof(systemName)) : systemName.Trim();
        Mode = mode.Trim().ToLowerInvariant();
        if (Mode is not ("dmr" or "p25" or "nxdn" or "analog"))
            throw new ArgumentException($"Unsupported channel mode '{mode}'.", nameof(mode));
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "A channel destination ID must be non-zero.");
        if (Mode == "nxdn" && destinationId > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "An NXDN destination ID must fit in 16 bits.");
        if (Mode == "dmr" && slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot), "A DMR runtime slot must be zero or one.");

        DestinationId = destinationId;
        Slot = slot;
        RxOnly = rxOnly;
        EncryptionAlgorithm = string.IsNullOrWhiteSpace(encryptionAlgorithm)
            ? "none"
            : encryptionAlgorithm.Trim().ToLowerInvariant();
        EncryptionKeyId = string.IsNullOrWhiteSpace(encryptionKeyId) ? null : encryptionKeyId.Trim();
        SelectableEncryption = selectableEncryption;
    }

    public string Name { get; }
    public string SystemName { get; }
    public string Mode { get; }
    public uint DestinationId { get; }
    public byte Slot { get; }
    public bool RxOnly { get; }
    public string EncryptionAlgorithm { get; }
    public string? EncryptionKeyId { get; }
    public bool SelectableEncryption { get; }
    public bool IsEncrypted => EncryptionAlgorithm is not ("none" or "clear" or "unencrypted");

    public static ChannelRuntimeDefinition FromConfiguration(ChannelConfiguration channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!uint.TryParse(channel.Tgid, out uint destinationId))
            throw new InvalidDataException($"Channel '{channel.Name}' has a non-numeric destination ID '{channel.Tgid}'.");

        string mode = channel.Mode.Trim().ToLowerInvariant();
        byte slot = mode == "dmr"
            ? channel.Slot switch
            {
                1 => (byte)0,
                2 => (byte)1,
                _ => throw new InvalidDataException($"DMR channel '{channel.Name}' must use slot 1 or 2.")
            }
            : (byte)0;

        return new ChannelRuntimeDefinition(
            channel.Name,
            channel.System,
            mode,
            destinationId,
            slot,
            channel.RxOnly,
            channel.Algo,
            channel.KeyId,
            channel.SelectableEncryption);
    }
}

// Runtime call state for one configured channel. It is intentionally free of
// UI and protocol types so WPF and Avalonia can consume the same state model.
public sealed class ChannelRuntime : INotifyPropertyChanged
{
    private ChannelRuntimeState state = ChannelRuntimeState.Idle;
    private uint? sourceId;
    private uint? streamId;
    private string? faultMessage;
    private DateTimeOffset? lastActivity;

    public ChannelRuntime(ChannelRuntimeDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChannelRuntimeDefinition Definition { get; }
    public ChannelRuntimeState State => state;
    public uint? SourceId => sourceId;
    public uint? StreamId => streamId;
    public string? FaultMessage => faultMessage;
    public DateTimeOffset? LastActivity => lastActivity;
    public string StateText => state switch
    {
        ChannelRuntimeState.Receiving when sourceId is not null => $"Receiving from {sourceId} (stream {streamId})",
        ChannelRuntimeState.Transmitting when streamId is not null => $"Transmitting (stream {streamId})",
        ChannelRuntimeState.Faulted when !string.IsNullOrWhiteSpace(faultMessage) => $"Faulted: {faultMessage}",
        _ => state.ToString()
    };

    public void MarkReceiving(uint sourceId, uint streamId, DateTimeOffset? activity = null)
    {
        if (sourceId == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        DateTimeOffset nextActivity = activity ?? DateTimeOffset.UtcNow;
        if (state == ChannelRuntimeState.Receiving &&
            this.sourceId == sourceId &&
            this.streamId == streamId)
        {
            lastActivity = nextActivity;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastActivity)));
            return;
        }

        state = ChannelRuntimeState.Receiving;
        this.sourceId = sourceId;
        this.streamId = streamId;
        faultMessage = null;
        lastActivity = nextActivity;
        NotifyStateChanged();
    }

    public void MarkTransmitting(uint streamId, DateTimeOffset? activity = null)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        state = ChannelRuntimeState.Transmitting;
        sourceId = null;
        this.streamId = streamId;
        faultMessage = null;
        lastActivity = activity ?? DateTimeOffset.UtcNow;
        NotifyStateChanged();
    }

    public void MarkIdle(DateTimeOffset? activity = null)
    {
        state = ChannelRuntimeState.Idle;
        sourceId = null;
        streamId = null;
        faultMessage = null;
        lastActivity = activity ?? DateTimeOffset.UtcNow;
        NotifyStateChanged();
    }

    public void MarkFault(string message, DateTimeOffset? activity = null)
    {
        faultMessage = string.IsNullOrWhiteSpace(message) ? "Unknown error" : message.Trim();
        state = ChannelRuntimeState.Faulted;
        lastActivity = activity ?? DateTimeOffset.UtcNow;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StreamId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FaultMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastActivity)));
    }
}
