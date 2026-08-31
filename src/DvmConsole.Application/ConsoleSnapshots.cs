using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed record ConfigurationReference(
    ConfigurationId Id,
    ConfigurationRevision Revision);

public sealed record SystemDescriptor(
    SystemId Id,
    string Name,
    string Protocol);

public sealed record ZoneDescriptor(
    ZoneId Id,
    string Name,
    IReadOnlyList<ChannelId> Channels);

public sealed record ChannelDescriptor(
    ChannelId Id,
    SystemId SystemId,
    ZoneId ZoneId,
    string Name,
    uint DestinationId,
    string Protocol,
    byte Slot,
    bool ReceiveOnly,
    bool AllowsTransmitDuringReceive = false);

/// <summary>
/// Immutable media-session identity and configuration used by receive
/// decoders without retaining a presentation object.
/// </summary>
public sealed record ReceiveChannelDescriptor(
    ChannelId Id,
    DvmConsole.Core.Runtime.ChannelRuntimeDefinition Definition);

public sealed record ConsoleTopologySnapshot(
    ConfigurationReference? Configuration,
    IReadOnlyList<SystemDescriptor> Systems,
    IReadOnlyList<ZoneDescriptor> Zones,
    IReadOnlyList<ChannelDescriptor> Channels)
{
    public static ConsoleTopologySnapshot Empty { get; } = new(null, [], [], []);
}

public enum TargetAuthorityState
{
    Pending,
    Available,
    Unavailable
}

public sealed record ChannelPatchMembership(
    PatchId Id,
    string Name,
    bool IsEnabled,
    bool IsOneWay,
    bool IsSource);

public sealed record ChannelControlSnapshot(
    ChannelId Id,
    DvmConsole.Core.Runtime.ChannelRuntimeState RuntimeState,
    string StateText,
    string LastCaller,
    bool ReceiveEnabled,
    bool ReceiveActive,
    bool Transmitting,
    bool TransmitSelected,
    bool PageSelected,
    bool AlertSelected,
    bool Recording,
    bool RecordingFinalizing,
    string? RecordingFault,
    bool TarArmed,
    string? OutputRoute,
    double Gain,
    double Balance,
    string? EffectiveMuteReason,
    TargetAuthorityState Authority,
    string? AuthorityReason,
    bool ObservedReceiveEncrypted,
    bool SelectedTransmitEncrypted,
    bool TransmitKeyAvailable,
    IReadOnlyList<ChannelPatchMembership> Patches,
    string? PendingOperation,
    string? Fault,
    bool RecordingPlayback = false,
    bool TransmitEncryptionConfigured = false,
    bool TransmitEncryptionSelectable = false)
{
    public bool HasSameContent(ChannelControlSnapshot? other)
        => other is not null &&
           Id == other.Id &&
           RuntimeState == other.RuntimeState &&
           string.Equals(StateText, other.StateText, StringComparison.Ordinal) &&
           string.Equals(LastCaller, other.LastCaller, StringComparison.Ordinal) &&
           ReceiveEnabled == other.ReceiveEnabled &&
           ReceiveActive == other.ReceiveActive &&
           Transmitting == other.Transmitting &&
           TransmitSelected == other.TransmitSelected &&
           PageSelected == other.PageSelected &&
           AlertSelected == other.AlertSelected &&
           Recording == other.Recording &&
           RecordingFinalizing == other.RecordingFinalizing &&
           string.Equals(RecordingFault, other.RecordingFault, StringComparison.Ordinal) &&
           TarArmed == other.TarArmed &&
           string.Equals(OutputRoute, other.OutputRoute, StringComparison.Ordinal) &&
           Gain.Equals(other.Gain) &&
           Balance.Equals(other.Balance) &&
           string.Equals(EffectiveMuteReason, other.EffectiveMuteReason, StringComparison.Ordinal) &&
           Authority == other.Authority &&
           string.Equals(AuthorityReason, other.AuthorityReason, StringComparison.Ordinal) &&
           ObservedReceiveEncrypted == other.ObservedReceiveEncrypted &&
           SelectedTransmitEncrypted == other.SelectedTransmitEncrypted &&
           TransmitKeyAvailable == other.TransmitKeyAvailable &&
           Patches.SequenceEqual(other.Patches) &&
           string.Equals(PendingOperation, other.PendingOperation, StringComparison.Ordinal) &&
           string.Equals(Fault, other.Fault, StringComparison.Ordinal) &&
           RecordingPlayback == other.RecordingPlayback &&
           TransmitEncryptionConfigured == other.TransmitEncryptionConfigured &&
           TransmitEncryptionSelectable == other.TransmitEncryptionSelectable;
}

public sealed record ConsoleRuntimeSnapshot(
    long Revision,
    ConfigurationReference? RunningConfiguration,
    IReadOnlyDictionary<ChannelId, ChannelControlSnapshot> Channels,
    bool IsQuiescing,
    string StatusText)
{
    public static ConsoleRuntimeSnapshot Empty { get; } = new(
        0,
        null,
        new Dictionary<ChannelId, ChannelControlSnapshot>(),
        false,
        string.Empty);

    public bool HasSameContent(ConsoleRuntimeSnapshot? other)
    {
        if (other is null ||
            !Equals(RunningConfiguration, other.RunningConfiguration) ||
            IsQuiescing != other.IsQuiescing ||
            !string.Equals(StatusText, other.StatusText, StringComparison.Ordinal) ||
            Channels.Count != other.Channels.Count)
        {
            return false;
        }

        foreach ((ChannelId id, ChannelControlSnapshot channel) in Channels)
        {
            if (!other.Channels.TryGetValue(id, out ChannelControlSnapshot? candidate) ||
                !channel.HasSameContent(candidate))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ConsoleSnapshotChangedEventArgs(
    ConsoleRuntimeSnapshot previous,
    ConsoleRuntimeSnapshot current) : EventArgs
{
    public ConsoleRuntimeSnapshot Previous { get; } = previous;
    public ConsoleRuntimeSnapshot Current { get; } = current;
}

public sealed record ChannelMeterSample(
    ChannelId ChannelId,
    double Rms,
    double Peak,
    DateTimeOffset SampledAt);

public enum ConsoleLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

public sealed record ConsoleLogEvent(
    DateTimeOffset Timestamp,
    ConsoleLogLevel Level,
    string Category,
    string Message,
    Exception? Exception = null);
