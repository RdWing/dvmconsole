// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

/// <summary>Shared submission and bounded acknowledgement tracking. Never retries or replays commands.</summary>
internal sealed class ConsoleSubscriberCommandDispatcher
{
    internal static readonly TimeSpan AcknowledgementWindow = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly IClock clock;
    private readonly int historyLimit;
    private readonly Dictionary<RequestKey, PendingRequest> pending = [];
    private ImmutableList<ConsoleSubscriberCommandResult> history = [];
    private DateTimeOffset attributionUncertainUntil;

    public ConsoleSubscriberCommandDispatcher(IClock clock, int historyLimit = 100)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        this.historyLimit = historyLimit;
    }

    public event EventHandler? HistoryChanged;
    public ImmutableList<ConsoleSubscriberCommandResult> History => Volatile.Read(ref history);

    public ConsoleSubscriberCommandResult Submit(SystemId system, IRadioTrafficEndpoint radio,
        IRadioSubscriberCommandEndpoint endpoint, ConsoleSubscriberCommand command, uint destinationId)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        string? failure = destinationId is 0 or > 0xFFFFFF ? "Enter a P25 subscriber RID from 1 to 16777215."
            : !radio.IsConnected ? $"{radio.Name} is not connected to an FNE."
            : radio.SourceId is not (> 0 and <= 0xFFFFFF) ? $"{radio.Name} does not have a configured source RID." : null;
        var result = new ConsoleSubscriberCommandResult(clock.UtcNow, system, command, destinationId, false,
            failure ?? "Sending command.")
        { SystemName = radio.Name, StatusText = failure ?? "Sending command." };
        var key = new RequestKey(system, command, destinationId);
        lock (sync)
        {
            ExpireCore();
            if (failure is null)
            {
                bool repeated = pending.TryGetValue(key, out var previous);
                bool ambiguous = repeated || clock.UtcNow < attributionUncertainUntil;
                if (previous is { Completed: false }) Update(previous.Id, entry => entry with
                {
                    Acknowledgement = ConsoleSubscriberAcknowledgementState.Ambiguous,
                    Detail = "Sent; repeated command responses cannot be attributed."
                });
                result = result with
                {
                    Acknowledgement = ambiguous
                    ? ConsoleSubscriberAcknowledgementState.Ambiguous : ConsoleSubscriberAcknowledgementState.Pending
                };
                pending[key] = new(result.Id, clock.UtcNow + AcknowledgementWindow, ambiguous);
            }
            var next = history.Insert(0, result);
            if (next.Count > historyLimit)
            {
                Guid removed = next[^1].Id;
                foreach (var item in pending.Where(item => item.Value.Id == removed).ToArray())
                {
                    // Eviction must not make a rapid repeat look like a new,
                    // unambiguous request. Bound memory and suppress attribution
                    // until any reply to the discarded attempt would expire.
                    if (item.Value.ExpiresAt > attributionUncertainUntil)
                        attributionUncertainUntil = item.Value.ExpiresAt;
                    pending.Remove(item.Key);
                }
                next = next.RemoveAt(historyLimit);
            }
            Volatile.Write(ref history, next);
        }
        // The pending entry exists before invoking a transport: a local transport
        // may publish a response synchronously. Do not hold our lock across send.
        if (failure is null)
        {
            try { endpoint.SendSubscriberCommand(command, destinationId); }
            catch (Exception exception) { failure = $"Unable to send command: {exception.Message}"; }
            lock (sync)
            {
                if (failure is not null && pending.TryGetValue(key, out var current) && current.Id == result.Id)
                    pending.Remove(key);
                result = Update(result.Id, entry => failure is not null
                    ? entry with
                    {
                        Submitted = false,
                        Acknowledgement = ConsoleSubscriberAcknowledgementState.None,
                        AcknowledgedAt = null,
                        Detail = failure,
                        StatusText = $"{radio.Name}: {failure}"
                    }
                    : entry with
                    {
                        Submitted = true,
                        Detail = Describe(entry.Acknowledgement),
                        StatusText = entry.Acknowledgement == ConsoleSubscriberAcknowledgementState.Received
                            ? entry.StatusText : $"{radio.Name}: {CommandName(command)} to RID {destinationId} sent."
                    }) ?? result;
            }
        }
        PublishChanged();
        return result;
    }

    public ConsoleSubscriberCommandResult? Acknowledge(ConsoleSubscriberAcknowledgement response)
    {
        ConsoleSubscriberCommandResult? result = null;
        bool changed;
        lock (sync)
        {
            changed = ExpireCore();
            var key = new RequestKey(response.System, response.Command, response.SubscriberId);
            if (pending.TryGetValue(key, out var request) && !request.Ambiguous && !request.Completed)
            {
                pending[key] = request with { Completed = true };
                result = Update(request.Id, entry => entry with
                {
                    Acknowledgement = ConsoleSubscriberAcknowledgementState.Received,
                    AcknowledgedAt = clock.UtcNow,
                    Detail = "Acknowledged by subscriber.",
                    StatusText = $"{entry.SystemName}: {CommandName(entry.Command)} acknowledged by RID {entry.DestinationId}."
                });
            }
        }
        if (changed || result is not null) PublishChanged();
        return result;
    }

    public void Interrupt(SystemId? system = null)
    {
        bool changed = false;
        lock (sync)
        {
            foreach (var item in pending.Where(item => system is null || item.Key.System == system).ToArray())
            {
                pending.Remove(item.Key);
                if (item.Value.Completed) continue;
                Update(item.Value.Id, entry => entry with
                { Acknowledgement = ConsoleSubscriberAcknowledgementState.Interrupted, Detail = "Sent; acknowledgement tracking stopped." });
                changed = true;
            }
        }
        if (changed) PublishChanged();
    }

    public void Expire()
    {
        bool changed;
        lock (sync) changed = ExpireCore();
        if (changed) PublishChanged();
    }

    private bool ExpireCore()
    {
        if (pending.Count == 0) return false;
        DateTimeOffset now = clock.UtcNow;
        bool changed = false;
        foreach (var item in pending.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            pending.Remove(item.Key);
            if (item.Value.Completed) continue;
            Update(item.Value.Id, entry => entry with
            { Acknowledgement = ConsoleSubscriberAcknowledgementState.TimedOut, Detail = "Sent; no acknowledgement received within 30 seconds." });
            changed = true;
        }
        return changed;
    }

    private ConsoleSubscriberCommandResult? Update(Guid id, Func<ConsoleSubscriberCommandResult, ConsoleSubscriberCommandResult> update)
    {
        int index = history.FindIndex(entry => entry.Id == id);
        if (index < 0) return null;
        var result = update(history[index]);
        Volatile.Write(ref history, history.SetItem(index, result));
        return result;
    }

    private void PublishChanged()
    {
        foreach (EventHandler observer in HistoryChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Presentation cannot interrupt command accounting. */ }
        }
    }

    private static string CommandName(ConsoleSubscriberCommand command)
        => command == ConsoleSubscriberCommand.RadioCheck ? "Radio check" : command.ToString();
    private static string Describe(ConsoleSubscriberAcknowledgementState state) => state switch
    {
        ConsoleSubscriberAcknowledgementState.Received => "Acknowledged by subscriber.",
        ConsoleSubscriberAcknowledgementState.Ambiguous => "Sent; acknowledgement cannot be attributed to this command.",
        ConsoleSubscriberAcknowledgementState.Interrupted => "Sent; acknowledgement tracking stopped.",
        ConsoleSubscriberAcknowledgementState.TimedOut => "Sent; no acknowledgement received within 30 seconds.",
        _ => "Sent; awaiting subscriber acknowledgement."
    };
    private readonly record struct RequestKey(SystemId System, ConsoleSubscriberCommand Command, uint Subscriber);
    private sealed record PendingRequest(Guid Id, DateTimeOffset ExpiresAt, bool Ambiguous, bool Completed = false);
}
