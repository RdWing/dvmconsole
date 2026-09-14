// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

internal sealed record ReceiveIngressSystem(SystemId Id, string Name, IReadOnlyList<ConsoleChannelState> Channels);

internal readonly record struct ReceiveIngressDecision(
    IRadioMediaFrame Traffic,
    DateTimeOffset ReceivedAt,
    long ReceivedTimestamp,
    ReceiveIngressRoutingDecision Routing,
    ReceiveCallEpisodeObservation? EpisodeObservation,
    ReceiveCallEpisodeSnapshot? EpisodeSnapshot,
    bool CanCoalescePresentation = false,
    IReadOnlyList<ChannelId>? CandidateChannelIds = null);

internal readonly record struct ReceiveIngressWorkItem(
    ReceiveIngressDecision Decision,
    ReceiveStateTargets PreEnqueuedAudioChannels,
    ReceiveStateTargets PreEnqueuedPatchChannels);
