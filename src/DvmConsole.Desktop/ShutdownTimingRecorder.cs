// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

// Captures the last application shutdown without keeping the debug workspace
// alive while its owned services are being released. The report contains only
// stable component names and durations; no configuration or traffic data.
internal sealed class ShutdownTimingRecorder
{
    internal const string FileName = "LastShutdown.log";
    private readonly object sync = new();
    private readonly string path;
    private readonly List<ShutdownTimingEntry> entries = [];
    private long started;
    private bool active;

    public ShutdownTimingRecorder(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        path = System.IO.Path.Combine(System.IO.Path.GetFullPath(appDataRoot), FileName);
    }

    public string Path => path;

    public void Begin()
    {
        lock (sync)
        {
            entries.Clear();
            started = Stopwatch.GetTimestamp();
            active = true;
        }
    }

    public async Task MeasureAsync(string name, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        long phaseStarted = Stopwatch.GetTimestamp();
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            Observe(name, Stopwatch.GetElapsedTime(phaseStarted));
        }
    }

    public void ObserveService(ConsoleSessionServiceDisposalTiming timing)
        => Observe($"service:{timing.Scope}/{timing.Name}", timing.Duration);

    public void Complete()
    {
        ShutdownTimingEntry[] captured;
        TimeSpan total;
        lock (sync)
        {
            if (!active)
                return;
            total = Stopwatch.GetElapsedTime(started);
            entries.Add(new ShutdownTimingEntry("total", total));
            captured = entries.ToArray();
            active = false;
        }

        try
        {
            AppDataFileProtection.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllLines(
                path,
                captured.Select(entry => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.Duration.TotalMilliseconds:0.0}\t{entry.Name}")));
            AppDataFileProtection.EnsureFile(path);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Shutdown timing persistence failed: {exception}");
        }
    }

    private void Observe(string name, TimeSpan duration)
    {
        lock (sync)
        {
            if (active)
                entries.Add(new ShutdownTimingEntry(name, duration));
        }
    }

    private readonly record struct ShutdownTimingEntry(string Name, TimeSpan Duration);
}
