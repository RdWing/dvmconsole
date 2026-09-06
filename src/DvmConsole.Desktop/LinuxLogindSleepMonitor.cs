// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Tmds.DBus.Protocol;

namespace DvmConsole.Desktop;

internal interface ILinuxSleepMonitor : IDisposable
{
    event EventHandler? Suspending;
    event EventHandler? Resumed;

    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Translates systemd-logind's cross-desktop PrepareForSleep signal into the
/// application lifecycle. Ubuntu, Mint, Debian, Fedora, and their common
/// desktop environments all expose this contract on the system bus.
/// </summary>
internal sealed class LinuxLogindSleepMonitor : ILinuxSleepMonitor
{
    private const string Service = "org.freedesktop.login1";
    private const string Path = "/org/freedesktop/login1";
    private const string Interface = "org.freedesktop.login1.Manager";

    private readonly object sync = new();
    private Connection? connection;
    private IDisposable? observer;
    private bool started;
    private bool disposed;

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The logind sleep monitor is available on Linux only.");

        lock (sync)
        {
            if (started)
                return;
            started = true;
        }

        var nextConnection = new Connection(
            Address.System ?? throw new InvalidOperationException("The system D-Bus address is unavailable."));
        try
        {
            await nextConnection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            IDisposable nextObserver = await nextConnection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = Service,
                    Path = Path,
                    Interface = Interface,
                    Member = "PrepareForSleep"
                },
                static (message, _) => message.GetBodyReader().ReadBool(),
                static (exception, preparingForSleep, _, state) =>
                    ((LinuxLogindSleepMonitor)state!).HandleNotification(
                        exception,
                        preparingForSleep),
                ObserverFlags.EmitOnConnectionDispose,
                null,
                this,
                false).ConfigureAwait(false);

            lock (sync)
            {
                if (disposed)
                {
                    nextObserver.Dispose();
                    nextConnection.Dispose();
                    return;
                }
                connection = nextConnection;
                observer = nextObserver;
            }
        }
        catch
        {
            nextConnection.Dispose();
            lock (sync)
                started = false;
            throw;
        }
    }

    public void Dispose()
    {
        IDisposable? currentObserver;
        Connection? currentConnection;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            currentObserver = observer;
            currentConnection = connection;
            observer = null;
            connection = null;
        }

        currentObserver?.Dispose();
        currentConnection?.Dispose();
    }

    private void HandleNotification(Exception? exception, bool preparingForSleep)
    {
        if (exception is not null)
            return;
        if (preparingForSleep)
            Suspending?.Invoke(this, EventArgs.Empty);
        else
            Resumed?.Invoke(this, EventArgs.Empty);
    }
}
