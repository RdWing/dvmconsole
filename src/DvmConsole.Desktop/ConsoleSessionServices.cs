namespace DvmConsole.Desktop;

/// <summary>
/// Ordered ownership registry for one loaded console session. Named scopes
/// describe responsibility without creating independent disposal islands:
/// every registration remains in one global reverse-construction order, and
/// concurrent disposal callers share one task.
/// </summary>
internal sealed class ConsoleSessionServices : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly List<ServiceRegistration> registrations = [];
    private readonly AsyncDisposal disposal = new();
    private bool acceptingRegistrations = true;

    public ConsoleSessionServices()
    {
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
        for (int index = owned.Length - 1; index >= 0; index--)
        {
            ServiceRegistration registration = owned[index];
            await cleanup.RunTaskAsync(
                () => registration.Dispose().AsTask()).ConfigureAwait(false);
        }
        cleanup.ThrowIfFailed();
    }

    private sealed record ServiceRegistration(
        string Scope,
        string Name,
        Func<ValueTask> Dispose);
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
