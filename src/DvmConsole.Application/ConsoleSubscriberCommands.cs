// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public enum ConsoleSubscriberCommand { Page, RadioCheck, Inhibit, Uninhibit }

/// <summary>Optional radio capability; protocol framing stays in the radio integration.</summary>
public interface IRadioSubscriberCommandEndpoint
{
    void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId);
}

/// <summary>A transport-validated acknowledgement addressed to this console.</summary>
public sealed record ConsoleSubscriberAcknowledgement(SystemId System, ConsoleSubscriberCommand Command, uint SubscriberId);

public interface IRadioSubscriberAcknowledgementSource
{
    event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;
}

public sealed record ConsoleSubscriberTarget(SystemId Id, string Name, bool IsConnected);
public enum ConsoleSubscriberAcknowledgementState { None, Pending, Received, TimedOut, Interrupted, Ambiguous }

public sealed record ConsoleSubscriberCommandResult(DateTimeOffset Timestamp, SystemId System,
    ConsoleSubscriberCommand Command, uint DestinationId, bool Submitted, string Detail)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ConsoleSubscriberAcknowledgementState Acknowledgement { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string SystemName { get; init; } = System.Value;
    public string StatusText { get; init; } = Detail;
}

public interface IConsoleSubscriberCommands
{
    IReadOnlyList<ConsoleSubscriberTarget> SubscriberTargets { get; }
    ImmutableList<ConsoleSubscriberCommandResult> SubscriberCommandHistory { get; }
    Task<ConsoleSubscriberCommandResult> SendSubscriberCommandAsync(SystemId system, ConsoleSubscriberCommand command,
        uint destinationId, CancellationToken cancellationToken = default);
}

public interface IConsoleSubscriberHistoryNotifications
{
    event EventHandler? SubscriberHistoryChanged;
}
