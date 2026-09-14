// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>Operational service results that supplement session-owned channel state.</summary>
public sealed record ChannelSnapshotContext(
    bool Recording = false,
    bool RecordingFinalizing = false,
    string? RecordingFault = null,
    string? EffectiveMuteReason = null,
    bool RecordingPlayback = false,
    IReadOnlyList<ChannelPatchMembership>? Patches = null,
    bool AllowTransmitControls = true);

/// <summary>Shared control projection; no presentation object supplies authoritative channel state.</summary>
public static class ConsoleChannelSnapshotProjector
{
    public static ChannelControlSnapshot Capture(ConsoleChannelState channel, RadioAliasIndex aliases,
        ChannelConfigurationAccess access, ChannelSnapshotContext context)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(context);
        ChannelOperatorSnapshot operation = channel.Operator.Snapshot;
        TargetAuthorityState authority = channel.Authority;
        return new ChannelControlSnapshot(
            channel.Id, channel.Runtime.State, StateText(channel, aliases, operation), LastCallerText(channel, aliases),
            operation.AudioEnabled, channel.ReceivePresentationOwner is not null,
            operation.TransmitEnabled, operation.TransmitSelected, operation.PageSelected, operation.AlertSelected,
            context.Recording, context.RecordingFinalizing, context.RecordingFault,
            operation.RecordingEnabled, operation.OutputRoute, operation.Gain, operation.Balance,
            context.EffectiveMuteReason, authority,
            authority == TargetAuthorityState.Unavailable ? access.AuthorityUnavailableReason : null,
            channel.Receive.ObservedEncrypted, operation.TransmitEncrypted, access.TransmitKeyAvailable,
            context.Patches?.ToImmutableArray() ?? [], null, null,
            context.RecordingPlayback, channel.Runtime.Definition.IsEncrypted,
            channel.Runtime.Definition.SelectableEncryption && context.AllowTransmitControls,
            operation.TransmitStarting, operation.TransmitStopping, channel.ReceivePresentationOwner?.PresentedSourceId, channel.Receive.LastCallerSource);
    }

    public static string LastCallerText(ConsoleChannelState channel, RadioAliasIndex aliases)
    {
        if (channel.Receive.LastCallerSource is not uint source) return "--";
        string alias = aliases.Find(source).Trim();
        return alias.Length == 0 ? source.ToString(CultureInfo.InvariantCulture) : alias;
    }

    public static string StateText(ConsoleChannelState channel, RadioAliasIndex aliases)
        => StateText(channel, aliases, channel.Operator.Snapshot);

    private static string StateText(ConsoleChannelState channel, RadioAliasIndex aliases, ChannelOperatorSnapshot operation)
    {
        if (operation.TransmitStarting) return "Starting PTT…";
        if (operation.TransmitStopping) return "Releasing PTT…";
        if (channel.Runtime.State == ChannelRuntimeState.Transmitting) return channel.Runtime.StateText;
        if (operation.AudioSuspended) return "RX muted during console transmit";
        ConsoleChannelState? owner = channel.ReceivePresentationOwner;
        if (owner?.PresentedSourceId is uint source)
        {
            string alias = aliases.Find(source);
            return !string.IsNullOrWhiteSpace(alias)
                ? $"Receiving from {alias} ({source}) (stream {owner.PresentedStreamId})"
                : $"Receiving from {source} (stream {owner.PresentedStreamId})";
        }
        if (!operation.AudioEnabled && channel.Runtime.State == ChannelRuntimeState.Receiving)
            return "Receive disabled";
        return channel.Runtime.StateText;
    }
}
