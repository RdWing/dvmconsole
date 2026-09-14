// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleSubscriberCommands, IConsoleSubscriberHistoryNotifications
{
    private readonly ConsoleSubscriberCommandDispatcher subscriberCommands;
    public ImmutableList<ConsoleSubscriberCommandResult> SubscriberCommandHistory => subscriberCommands.History;
    public IReadOnlyList<ConsoleSubscriberTarget> SubscriberTargets => radios.Sessions.Values
        .Where(radio => radio is IRadioSubscriberCommandEndpoint)
        .Select(radio => new ConsoleSubscriberTarget(radio.SystemId, radio.Name, radio.IsConnected)).ToArray();

    public event EventHandler? SubscriberHistoryChanged;

    private void OnSubscriberHistoryChanged(object? sender, EventArgs args)
    {
        if (IsStopping) return;
        foreach (EventHandler observer in SubscriberHistoryChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Detached presentation cannot interrupt subscriber accounting. */ }
        }
    }

    private void OnSubscriberAcknowledged(object? sender, ConsoleSubscriberAcknowledgement response)
        => radioLifecycle.OnSubscriberAcknowledged(sender, response);

    public async Task<ConsoleSubscriberCommandResult> SendSubscriberCommandAsync(SystemId system,
        ConsoleSubscriberCommand command, uint destinationId, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        if (destinationId is 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(destinationId), "Enter a P25 subscriber RID from 1 to 16777215.");
        if (!radios.Sessions.TryGetValue(system, out var radio) || radio is not IRadioSubscriberCommandEndpoint endpoint)
            throw new NotSupportedException("This system does not support subscriber commands.");
        long generation = state.Execution.Snapshot.ManualGeneration;
        ConsoleSubscriberCommandResult? result = null;
        await RunCommandAsync(async token =>
        {
            // A queued command must not acquire fresh foreground intent after a
            // background/interruption transition or configuration replacement.
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var execution = state.Execution.Snapshot;
                if (execution.State != ConsoleExecutionState.Foreground || execution.ManualGeneration != generation)
                    throw new OperationCanceledException("Subscriber command was superseded by a lifecycle transition.");
                result = subscriberCommands.Submit(system, radio, endpoint, command, destinationId);
                SetStatus(result.StatusText);
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
