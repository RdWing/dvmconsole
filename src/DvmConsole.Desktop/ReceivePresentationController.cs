// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;

namespace DvmConsole.Desktop;

internal interface IReceivePresentationPort
{
    bool IsDisposing { get; }
    bool HasUiThreadAccess { get; }
    long GetTimestamp();
    void Post(Action action);
    void Present(SystemViewModel system, SystemTrafficWorkItem workItem, bool publishDiagnostics);
    void ObserveTiming(string systemName, TimeSpan queueDelay, TimeSpan applyDuration) { }
}

internal sealed class ReceivePresentationPort(
    Func<bool> isDisposing,
    Func<bool> hasUiThreadAccess,
    Action<Action> post,
    Action<SystemViewModel, SystemTrafficWorkItem, bool> present,
    Func<long>? getTimestamp = null,
    Action<string, TimeSpan, TimeSpan>? observeTiming = null) : IReceivePresentationPort
{
    private readonly Func<long> clock = getTimestamp ?? Stopwatch.GetTimestamp;

    public bool IsDisposing => isDisposing();
    public bool HasUiThreadAccess => hasUiThreadAccess();
    public long GetTimestamp() => clock();
    public void ObserveTiming(string systemName, TimeSpan queueDelay, TimeSpan applyDuration)
    {
        try
        {
            observeTiming?.Invoke(systemName, queueDelay, applyDuration);
        }
        catch
        {
            // A diagnostic sink cannot strand the remaining presentation batch.
        }
    }
    public void Post(Action action) => post(action);
    public void Present(
        SystemViewModel system,
        SystemTrafficWorkItem workItem,
        bool publishDiagnostics)
        => present(system, workItem, publishDiagnostics);
}

internal sealed class ReceivePresentationController
{
    private const int MaximumBatchSize = 64;
    private static readonly TimeSpan DefaultMaximumBatchDuration =
        TimeSpan.FromMilliseconds(4);
    private readonly object sync = new();
    private readonly Dictionary<SystemViewModel, SystemTrafficBuffer> pendingBySystem = [];
    private readonly HashSet<SystemViewModel> scheduledSystems = [];
    private readonly IReceivePresentationPort port;
    private readonly TimeSpan maximumBatchDuration;

    public ReceivePresentationController(
        IReceivePresentationPort port,
        TimeSpan? maximumBatchDuration = null)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        this.maximumBatchDuration = maximumBatchDuration ?? DefaultMaximumBatchDuration;
        if (this.maximumBatchDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchDuration));
    }

    public void Present(SystemViewModel system, SystemTrafficWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (port.IsDisposing)
            return;
        workItem = workItem with { PresentationQueuedTimestamp = port.GetTimestamp() };
        if (port.HasUiThreadAccess)
        {
            Apply(system, workItem, true);
            return;
        }

        bool schedule;
        lock (sync)
        {
            if (!pendingBySystem.TryGetValue(system, out SystemTrafficBuffer? pending))
            {
                pending = new SystemTrafficBuffer();
                pendingBySystem.Add(system, pending);
            }
            long droppedBefore = pending.DroppedCount;
            pending.Enqueue(workItem);
            system.RecordDroppedSystemTraffic(pending.DroppedCount - droppedBefore);
            schedule = scheduledSystems.Add(system);
        }

        if (schedule)
            port.Post(() => Drain(system));
    }

    private void Apply(SystemViewModel system, SystemTrafficWorkItem workItem, bool publishDiagnostics)
    {
        long started = port.GetTimestamp();
        port.Present(system, workItem, publishDiagnostics);
        TimeSpan duration = Stopwatch.GetElapsedTime(started, port.GetTimestamp());
        TimeSpan queueDelay = Stopwatch.GetElapsedTime(workItem.PresentationQueuedTimestamp, started);
        port.ObserveTiming(system.Name, queueDelay, duration);
    }

    private void Drain(SystemViewModel system)
    {
        if (port.IsDisposing)
        {
            lock (sync)
            {
                pendingBySystem.Remove(system);
                scheduledSystems.Remove(system);
            }
            return;
        }

        int processed = 0;
        long batchStarted = port.GetTimestamp();
        while (true)
        {
            SystemTrafficWorkItem? workItem = null;
            bool empty;
            lock (sync)
            {
                empty = !pendingBySystem.TryGetValue(system, out SystemTrafficBuffer? pending) ||
                    !pending.TryDequeue(out workItem);
                if (empty)
                {
                    pendingBySystem.Remove(system);
                    scheduledSystems.Remove(system);
                }
            }

            if (empty)
                return;

            Apply(system, workItem!.Value, false);
            processed++;

            if (processed >= MaximumBatchSize ||
                Stopwatch.GetElapsedTime(batchStarted, port.GetTimestamp()) >= maximumBatchDuration)
            {
                port.Post(() => Drain(system));
                return;
            }
        }
    }
}
