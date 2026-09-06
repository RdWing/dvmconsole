// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Collections.Concurrent;

namespace DvmConsole.Desktop;

/// <summary>
/// Ordered ownership registry for one loaded console session. Named scopes
/// describe responsibility without creating independent disposal islands:
/// every registration remains in one global reverse-construction order, and
/// concurrent disposal callers share one task.
/// </summary>
internal sealed class ConsoleSessionServices : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDisposalStepTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultOverallDisposalTimeout = TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private readonly List<ServiceRegistration> registrations = [];
    private readonly ConcurrentDictionary<long, AbandonedOperation> abandonedOperations = [];
    private readonly AsyncDisposal disposal = new();
    private readonly Action<ConsoleSessionServiceDisposalTiming>? disposalObserved;
    private readonly TimeSpan disposalStepTimeout;
    private readonly TimeSpan overallDisposalTimeout;
    private bool acceptingRegistrations = true;
    private long nextAbandonedOperationId;

    public ConsoleSessionServices(
        Action<ConsoleSessionServiceDisposalTiming>? disposalObserved = null,
        TimeSpan? disposalStepTimeout = null,
        TimeSpan? overallDisposalTimeout = null)
    {
        this.disposalObserved = disposalObserved;
        this.disposalStepTimeout = disposalStepTimeout ?? DefaultDisposalStepTimeout;
        this.overallDisposalTimeout = overallDisposalTimeout ?? DefaultOverallDisposalTimeout;
        if (this.disposalStepTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(disposalStepTimeout));
        if (this.overallDisposalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(overallDisposalTimeout));
        Timers = new ConsoleSessionServiceScope(this, "timers");
        Audio = new ConsoleSessionServiceScope(this, "audio");
        Receive = new ConsoleSessionServiceScope(this, "receive");
        Transmit = new ConsoleSessionServiceScope(this, "transmit");
        Recording = new ConsoleSessionServiceScope(this, "recording");
        Patch = new ConsoleSessionServiceScope(this, "patch");
        Connection = new ConsoleSessionServiceScope(this, "connection");
        Presentation = new ConsoleSessionServiceScope(this, "presentation");
    }

    public ConsoleSessionServiceScope Timers { get; }

    public ConsoleSessionServiceScope Audio { get; }

    public ConsoleSessionServiceScope Receive { get; }

    public ConsoleSessionServiceScope Transmit { get; }

    public ConsoleSessionServiceScope Recording { get; }

    public ConsoleSessionServiceScope Patch { get; }

    public ConsoleSessionServiceScope Connection { get; }

    public ConsoleSessionServiceScope Presentation { get; }

    public int Count
    {
        get
        {
            lock (sync)
                return registrations.Count;
        }
    }

    internal int AbandonedOperationCount => abandonedOperations.Count;

    public IReadOnlyList<ConsoleSessionServiceOwnership> SnapshotOwnership()
    {
        lock (sync)
        {
            return registrations
                .Select(registration => new ConsoleSessionServiceOwnership(
                    registration.Scope,
                    registration.Name))
                .ToArray();
        }
    }

    internal void Register(
        string scope,
        string name,
        Func<ValueTask> dispose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dispose);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!acceptingRegistrations, this);
            registrations.Add(new ServiceRegistration(
                scope.Trim(),
                name.Trim(),
                dispose));
        }
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        ServiceRegistration[] owned;
        lock (sync)
        {
            acceptingRegistrations = false;
            owned = registrations.ToArray();
            registrations.Clear();
        }

        var cleanup = new AsyncCleanup();
        long disposalStarted = Stopwatch.GetTimestamp();
        for (int index = owned.Length - 1; index >= 0; index--)
        {
            ServiceRegistration registration = owned[index];
            long started = Stopwatch.GetTimestamp();
            Task operation = Task.Run(
                async () => await registration.Dispose().ConfigureAwait(false),
                CancellationToken.None);
            TimeSpan remaining = overallDisposalTimeout - Stopwatch.GetElapsedTime(disposalStarted);
            TimeSpan budget = remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : Min(disposalStepTimeout, remaining);
            if (budget == TimeSpan.Zero)
            {
                TrackAbandoned(registration, operation);
                cleanup.Capture(new TimeoutException(
                    $"Session cleanup deadline elapsed before '{registration.Scope}/{registration.Name}' completed."));
            }
            else
            {
                try
                {
                    await operation.WaitAsync(budget).ConfigureAwait(false);
                }
                catch (TimeoutException exception) when (!operation.IsCompleted)
                {
                    TrackAbandoned(registration, operation);
                    cleanup.Capture(new TimeoutException(
                        $"Session cleanup '{registration.Scope}/{registration.Name}' exceeded " +
                        $"its {budget.TotalMilliseconds:0} ms budget.",
                        exception));
                }
                catch (Exception exception)
                {
                    cleanup.Capture(new InvalidOperationException(
                        $"Session cleanup '{registration.Scope}/{registration.Name}' failed.",
                        exception));
                }
            }
            try
            {
                disposalObserved?.Invoke(new ConsoleSessionServiceDisposalTiming(
                    registration.Scope,
                    registration.Name,
                    Stopwatch.GetElapsedTime(started)));
            }
            catch
            {
                // Shutdown timing is diagnostic and must never interrupt cleanup.
            }
        }
        cleanup.ThrowIfFailed();
    }

    private void TrackAbandoned(ServiceRegistration registration, Task operation)
    {
        long id = Interlocked.Increment(ref nextAbandonedOperationId);
        abandonedOperations[id] = new AbandonedOperation(
            registration.Scope,
            registration.Name,
            operation);
        TaskObservation.Observe(ObserveAbandonedAsync(id));
    }

    private async Task ObserveAbandonedAsync(long id)
    {
        if (!abandonedOperations.TryGetValue(id, out AbandonedOperation? abandoned))
            return;

        try
        {
            await abandoned.Operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Abandoned session cleanup '{0}/{1}' eventually failed: {2}",
                abandoned.Scope,
                abandoned.Name,
                exception);
        }
        finally
        {
            abandonedOperations.TryRemove(id, out _);
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second)
        => first <= second ? first : second;

    private sealed record ServiceRegistration(
        string Scope,
        string Name,
        Func<ValueTask> Dispose);

    private sealed record AbandonedOperation(
        string Scope,
        string Name,
        Task Operation);
}

internal sealed class ConsoleSessionServiceScope
{
    private readonly ConsoleSessionServices owner;

    internal ConsoleSessionServiceScope(ConsoleSessionServices owner, string name)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public string Name { get; }

    public T Own<T>(string name, T service)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(service);
        Register(name, () =>
        {
            service.Dispose();
            return ValueTask.CompletedTask;
        });
        return service;
    }

    public T OwnAsync<T>(string name, T service)
        where T : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(service);
        Register(name, service.DisposeAsync);
        return service;
    }

    public void Register(string name, Func<ValueTask> dispose)
        => owner.Register(Name, name, dispose);
}

internal readonly record struct ConsoleSessionServiceOwnership(
    string Scope,
    string Name);

internal readonly record struct ConsoleSessionServiceDisposalTiming(
    string Scope,
    string Name,
    TimeSpan Duration);
