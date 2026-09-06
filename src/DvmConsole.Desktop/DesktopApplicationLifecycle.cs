// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using DvmConsole.Application;

namespace DvmConsole.Desktop;

/// <summary>
/// Converts the Avalonia desktop window lifecycle into the portable host
/// contract. Mobile hosts can supply their own foreground/background adapter.
/// </summary>
internal sealed class DesktopApplicationLifecycle : IApplicationLifecycle, IDisposable
{
    private readonly Window window;
    private readonly ILinuxSleepMonitor? sleepMonitor;
    private int active;
    private int stopping;
    private int disposed;

    public DesktopApplicationLifecycle(Window window)
        : this(window, OperatingSystem.IsLinux() ? new LinuxLogindSleepMonitor() : null)
    {
    }

    internal DesktopApplicationLifecycle(Window window, ILinuxSleepMonitor? sleepMonitor)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.sleepMonitor = sleepMonitor;
        window.Activated += HandleActivated;
        window.Deactivated += HandleDeactivated;
        window.Opened += HandleOpened;
        window.Closing += HandleClosing;
        if (sleepMonitor is not null)
        {
            sleepMonitor.Suspending += HandleSuspending;
            sleepMonitor.Resumed += HandleResumed;
        }
    }

    public bool IsActive => Volatile.Read(ref active) != 0;

    public event EventHandler? Activated;
    public event EventHandler? Deactivated;
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler? Stopping;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        window.Activated -= HandleActivated;
        window.Deactivated -= HandleDeactivated;
        window.Opened -= HandleOpened;
        window.Closing -= HandleClosing;
        if (sleepMonitor is not null)
        {
            sleepMonitor.Suspending -= HandleSuspending;
            sleepMonitor.Resumed -= HandleResumed;
            sleepMonitor.Dispose();
        }
    }

    private void HandleActivated(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref active, 1) == 0)
            Activated?.Invoke(this, EventArgs.Empty);
    }

    private void HandleDeactivated(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref active, 0) != 0)
            Deactivated?.Invoke(this, EventArgs.Empty);
    }

    private void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
            Stopping?.Invoke(this, EventArgs.Empty);
    }

    private async void HandleOpened(object? sender, EventArgs args)
    {
        if (sleepMonitor is null)
            return;
        try
        {
            await sleepMonitor.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Linux suspend/resume monitoring", exception);
        }
    }

    private void HandleSuspending(object? sender, EventArgs args)
        => NotifySuspending();

    private void HandleResumed(object? sender, EventArgs args)
        => NotifyResumed();

    internal void NotifySuspending()
        => Suspending?.Invoke(this, EventArgs.Empty);

    internal void NotifyResumed()
        => Resumed?.Invoke(this, EventArgs.Empty);
}
