// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Adapts the desktop radio/audio graph to the portable session
// boundary. It publishes only immutable, ID-keyed state; Application owns the
// actual session façade, revisioning, telemetry channels, and lifecycle.
internal sealed class DesktopConsoleSessionRuntimeAdapter : IConsoleSessionRuntimeAdapter, IConsoleSessionReactivation
{
    private readonly MainWindowViewModel owner;
    private readonly IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels;
    private readonly ConsoleTopologySnapshot topology;
    private readonly Func<CancellationToken, ValueTask> quiesce;
    private readonly Func<CancellationToken, ValueTask> flushSettings;
    private readonly IClock clock;
    private readonly object snapshotSync = new();
    private readonly ConsoleSnapshotState snapshots;
    private readonly SingleFlightAsyncAction controlPublication;
    private int disposed;

    public DesktopConsoleSessionRuntimeAdapter(
        MainWindowViewModel owner,
        Func<CancellationToken, ValueTask> quiesce,
        Func<CancellationToken, ValueTask> flushSettings,
        IClock? clock = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.quiesce = quiesce ?? throw new ArgumentNullException(nameof(quiesce));
        this.flushSettings = flushSettings ?? throw new ArgumentNullException(nameof(flushSettings));
        this.clock = clock ?? SystemClock.Instance;
        controlPublication = new SingleFlightAsyncAction(_ =>
        {
            PublishControlStateInvalidation();
            return Task.CompletedTask;
        });

        channels = owner.OperationalRuntime.Channels;
        topology = DesktopConsoleSnapshotProjector.BuildTopology(owner);
        var context = owner.CreateSnapshotContextSource();
        snapshots = owner.OperationalRuntime.GetOrCreateSnapshots(topology, context, owner.SessionStatus);
        snapshots.Changed += HandleSnapshotChanged;
        Commands = owner.OperationalRuntime.Commands;

        owner.DebugLogPublished += HandleDebugLogPublished;
        foreach (ConsoleChannelState channel in channels.Values)
        {
            channel.Meter.Changed += HandleMeterChanged;
        }
    }

    public IReadOnlyList<ConsoleCallHistoryRecord> History => owner.ApplicationHistory;
    public IConsoleCommands Commands { get; }

    public event EventHandler? ControlStateInvalidated;
    public event EventHandler<ChannelMeterSample>? MeterSampled;

    public event EventHandler<ConsoleLogEvent>? LogPublished;

    public ConsoleTopologySnapshot CaptureTopology() => topology;
    public ConsoleRuntimeSnapshot CaptureSnapshot()
    {
        lock (snapshotSync)
        {
            return snapshots.Capture();
        }
    }

    public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot previous)
    {
        lock (snapshotSync)
            return snapshots.CaptureUpdate(previous);
    }

    public ValueTask QuiesceAsync(CancellationToken cancellationToken)
        => quiesce(cancellationToken);

    public void ReactivateAfterFailedReplacement()
        => owner.ResumeSessionInputAfterFailedTransition();

    public ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
        => flushSettings(cancellationToken);

    public ValueTask DisposeAsync()
        => DisposeProjectionAsync();

    private void HandleSnapshotChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) controlPublication.Request();
    }

    private void HandleMeterChanged(object? sender, ChannelAudioMeterLevels levels)
    {
        if (Volatile.Read(ref disposed) != 0 || sender is not ChannelMeterState meter) return;
        MeterSampled?.Invoke(this, new ChannelMeterSample(meter.ChannelId, levels.Rms, levels.Peak, clock.UtcNow));
    }

    private void PublishControlStateInvalidation()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        foreach (EventHandler observer in ControlStateInvalidated?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* One observer cannot interrupt runtime publication. */ }
        }
    }

    private ValueTask DisposeProjectionAsync()
    {
        lock (snapshotSync)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return controlPublication.DisposeAsync();

            snapshots.Changed -= HandleSnapshotChanged;
            owner.DebugLogPublished -= HandleDebugLogPublished;
            foreach (ConsoleChannelState channel in channels.Values)
            {
                channel.Meter.Changed -= HandleMeterChanged;
            }
        }
        return controlPublication.DisposeAsync();
    }

    private void HandleDebugLogPublished(object? sender, DebugLogEntry entry)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        LogPublished?.Invoke(this, ProjectLog(entry));
    }

    internal static ConsoleLogEvent ProjectLog(DebugLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ConsoleLogLevel level = entry.Severity switch
        {
            DebugLogSeverity.Debug => ConsoleLogLevel.Debug,
            DebugLogSeverity.Info => ConsoleLogLevel.Information,
            DebugLogSeverity.Warning => ConsoleLogLevel.Warning,
            DebugLogSeverity.Error or DebugLogSeverity.Fatal => ConsoleLogLevel.Error,
            _ => ConsoleLogLevel.Information
        };
        return new ConsoleLogEvent(
            entry.Timestamp,
            level,
            entry.Source,
            entry.Message);
    }

}
