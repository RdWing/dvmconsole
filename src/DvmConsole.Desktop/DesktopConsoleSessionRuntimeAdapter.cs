// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Adapts the desktop radio/audio graph to the portable session
// boundary. It publishes only immutable, ID-keyed state; Application owns the
// actual session façade, revisioning, telemetry channels, and lifecycle.
internal sealed class DesktopConsoleSessionRuntimeAdapter : IConsoleSessionRuntimeAdapter
{
    internal enum ChannelProjectionChangeKind
    {
        None,
        Meter,
        Control
    }

    private static readonly HashSet<string> ControlProjectionProperties =
    [
        nameof(ChannelViewModel.State),
        nameof(ChannelViewModel.StateText),
        nameof(ChannelViewModel.LastCallerText),
        nameof(ChannelViewModel.IsAudioEnabled),
        nameof(ChannelViewModel.IsReceivePresentationActive),
        nameof(ChannelViewModel.IsTransmitting),
        nameof(ChannelViewModel.IsTransmitSelected),
        nameof(ChannelViewModel.IsPageSelected),
        nameof(ChannelViewModel.IsAlertSelected),
        nameof(ChannelViewModel.IsRecordingEnabled),
        nameof(ChannelViewModel.OutputDeviceIdText),
        nameof(ChannelViewModel.Volume),
        nameof(ChannelViewModel.StereoBalance),
        nameof(ChannelViewModel.TalkgroupAvailability),
        nameof(ChannelViewModel.ObservedReceiveEncrypted),
        nameof(ChannelViewModel.IsTransmitEncrypted),
        nameof(ChannelViewModel.CanTransmit)
    ];

    private static readonly HashSet<string> OwnerProjectionProperties =
    [
        nameof(MainWindowViewModel.StatusText),
        nameof(MainWindowViewModel.OutputMuted),
        nameof(MainWindowViewModel.SelectedSystemOutputMuted),
        nameof(MainWindowViewModel.SelectedZoneOutputMuted),
        "recordingPlaybackChannelId"
    ];

    private readonly MainWindowViewModel owner;
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channels;
    private readonly ConsoleTopologySnapshot topology;
    private readonly Func<CancellationToken, ValueTask> quiesce;
    private readonly Func<CancellationToken, ValueTask> flushSettings;
    private readonly IClock clock;
    private readonly object snapshotSync = new();
    private readonly HashSet<ChannelId> dirtyChannels = [];
    private IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> patchIndex =
        new Dictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>();
    private ConsoleRuntimeSnapshot? cachedSnapshot;
    private ConsoleRuntimeSnapshot? previousSnapshot;
    private IReadOnlyDictionary<ChannelId, ChannelControlSnapshot>? updateBaseChannels;
    private ChannelId[]? updatedChannelIds;
    private bool allChannelsDirty = true;
    private bool patchIndexDirty = true;
    private int controlInvalidationScheduled;
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

        channels = owner.Zones
            .SelectMany(zone => zone.Channels)
            .Concat(owner.Systems.SelectMany(system => system.Channels))
            .Distinct()
            .GroupBy(channel => new ChannelId(channel.SessionId))
            .ToDictionary(group => group.Key, group => group.First());
        topology = DesktopConsoleSnapshotProjector.BuildTopology(owner);
        Commands = new DesktopConsoleCommands(owner, channels);

        owner.PropertyChanged += HandleOwnerPropertyChanged;
        owner.RecordingStateChanged += HandleRecordingStateChanged;
        owner.DebugLogPublished += HandleDebugLogPublished;
        foreach (ChannelViewModel channel in channels.Values)
            channel.PropertyChanged += HandleChannelPropertyChanged;
        foreach (PatchGroupEditorViewModel group in owner.PatchGroups)
        {
            group.PropertyChanged += HandlePatchGroupPropertyChanged;
            group.MembershipChanged += HandlePatchGroupMembershipChanged;
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
            if (cachedSnapshot is not null)
                return cachedSnapshot;
            if (patchIndexDirty)
            {
                patchIndex = DesktopConsoleSnapshotProjector.BuildPatchIndex(owner);
                patchIndexDirty = false;
            }

            ChannelId[]? changed = allChannelsDirty ? null : dirtyChannels.ToArray();
            updateBaseChannels = previousSnapshot?.Channels;
            updatedChannelIds = changed;
            cachedSnapshot = DesktopConsoleSnapshotProjector.BuildSnapshot(
                owner,
                channels,
                0,
                previousSnapshot,
                changed,
                patchIndex);
            previousSnapshot = cachedSnapshot;
            allChannelsDirty = false;
            dirtyChannels.Clear();
            return cachedSnapshot;
        }
    }

    public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot previous)
    {
        lock (snapshotSync)
        {
            ConsoleRuntimeSnapshot current = CaptureSnapshot();
            return new ConsoleSnapshotUpdate(current,
                ReferenceEquals(previous.Channels, current.Channels) ? [] :
                ReferenceEquals(previous.Channels, updateBaseChannels) ? updatedChannelIds : null);
        }
    }

    public ValueTask QuiesceAsync(CancellationToken cancellationToken)
        => quiesce(cancellationToken);

    public ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
        => flushSettings(cancellationToken);

    public ValueTask DisposeAsync()
        => DisposeProjectionAsync();

    private void HandleOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0 &&
            (args.PropertyName is null || OwnerProjectionProperties.Contains(args.PropertyName)))
        {
            bool statusOnly = args.PropertyName == nameof(MainWindowViewModel.StatusText);
            RequestControlStateInvalidation(
                invalidateAllChannels: !statusOnly && args.PropertyName is (
                    null or
                    nameof(MainWindowViewModel.OutputMuted) or
                    nameof(MainWindowViewModel.SelectedSystemOutputMuted) or
                    nameof(MainWindowViewModel.SelectedZoneOutputMuted)),
                invalidateChannels: !statusOnly);
        }
    }

    private void HandleRecordingStateChanged(ChannelId channelId)
        => RequestControlStateInvalidation(channelId);

    private void HandlePatchGroupPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => RequestPatchProjectionInvalidation();

    private void HandlePatchGroupMembershipChanged(object? sender, EventArgs args)
        => RequestPatchProjectionInvalidation();

    private void RequestPatchProjectionInvalidation()
    {
        lock (snapshotSync)
            patchIndexDirty = true;
        RequestControlStateInvalidation(invalidateAllChannels: true);
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || sender is not ChannelViewModel channel)
            return;

        switch (ClassifyChannelProperty(args.PropertyName))
        {
            case ChannelProjectionChangeKind.Meter:
                AudioMeterState meter = channel.AudioMeter;
                MeterSampled?.Invoke(this, new ChannelMeterSample(
                    new ChannelId(channel.SessionId),
                    meter.Level,
                    meter.PeakLevel,
                    clock.UtcNow));
                break;
            case ChannelProjectionChangeKind.Control:
                RequestControlStateInvalidation(channel.Id);
                break;
        }
    }

    private void RequestControlStateInvalidation(
        ChannelId? channelId = null,
        bool invalidateAllChannels = false,
        bool invalidateChannels = true)
    {
        lock (snapshotSync)
        {
            cachedSnapshot = null;
            if (!invalidateChannels)
            {
                // Owner-only fields such as StatusText do not require any
                // channel projection work.
            }
            else if (invalidateAllChannels || channelId is null)
                allChannelsDirty = true;
            else if (!allChannelsDirty)
                dirtyChannels.Add(channelId.Value);
        }
        if (Volatile.Read(ref disposed) != 0 ||
            Interlocked.Exchange(ref controlInvalidationScheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref controlInvalidationScheduled, 0);
            if (Volatile.Read(ref disposed) != 0)
                return;

            foreach (EventHandler observer in
                     ControlStateInvalidated?.GetInvocationList().Cast<EventHandler>() ?? [])
            {
                try
                {
                    observer(this, EventArgs.Empty);
                }
                catch
                {
                    // A projection observer cannot block later subscribers or
                    // interrupt runtime invalidation.
                }
            }
        });
    }

    internal static ChannelProjectionChangeKind ClassifyChannelProperty(string? propertyName)
    {
        if (propertyName is null)
            return ChannelProjectionChangeKind.Control;
        if (propertyName == nameof(ChannelViewModel.AudioMeter))
            return ChannelProjectionChangeKind.Meter;
        return ControlProjectionProperties.Contains(propertyName)
            ? ChannelProjectionChangeKind.Control
            : ChannelProjectionChangeKind.None;
    }

    private ValueTask DisposeProjectionAsync()
    {
        lock (snapshotSync)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return ValueTask.CompletedTask;

            owner.PropertyChanged -= HandleOwnerPropertyChanged;
            owner.RecordingStateChanged -= HandleRecordingStateChanged;
            owner.DebugLogPublished -= HandleDebugLogPublished;
            foreach (ChannelViewModel channel in channels.Values)
                channel.PropertyChanged -= HandleChannelPropertyChanged;
            foreach (PatchGroupEditorViewModel group in owner.PatchGroups)
            {
                group.PropertyChanged -= HandlePatchGroupPropertyChanged;
                group.MembershipChanged -= HandlePatchGroupMembershipChanged;
            }
        }
        return ValueTask.CompletedTask;
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

    private sealed class DesktopConsoleCommands(
        MainWindowViewModel owner,
        IReadOnlyDictionary<ChannelId, ChannelViewModel> channels) : IConsoleCommands
    {
        public ValueTask SetReceiveEnabledAsync(
            ChannelId channelId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => owner.SetChannelReceiveEnabledAsync(GetChannel(channelId), enabled, cancellationToken);

        public async ValueTask<bool> BeginPttAsync(
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await owner.StartChannelTransmitAsync(GetChannel(channelId)).ConfigureAwait(false);
        }

        public async ValueTask EndPttAsync(
            ChannelId channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await owner.StopChannelTransmitAsync(GetChannel(channelId)).ConfigureAwait(false);
        }

        public ValueTask SetTransmitSelectedAsync(
            ChannelId channelId,
            bool selected,
            CancellationToken cancellationToken = default)
            => SetSelection(channelId, selected, owner.SetChannelTransmitSelection, cancellationToken);

        public ValueTask SetPageSelectedAsync(
            ChannelId channelId,
            bool selected,
            CancellationToken cancellationToken = default)
            => SetSelection(channelId, selected, owner.SetChannelPageSelection, cancellationToken);

        public ValueTask SetAlertSelectedAsync(
            ChannelId channelId,
            bool selected,
            CancellationToken cancellationToken = default)
            => SetSelection(channelId, selected, owner.SetChannelAlertSelection, cancellationToken);

        public ValueTask SetTransmitEncryptedAsync(
            ChannelId channelId,
            bool encrypted,
            CancellationToken cancellationToken = default)
            => SetSelection(channelId, encrypted, static (channel, value) => channel.SetTransmitEncrypted(value), cancellationToken);

        public ValueTask SetChannelGainAsync(
            ChannelId channelId,
            double gain,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetChannel(channelId).Volume = gain;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetChannelBalanceAsync(
            ChannelId channelId,
            double balance,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetChannel(channelId).StereoBalance = balance;
            return ValueTask.CompletedTask;
        }

        private ValueTask SetSelection(
            ChannelId channelId,
            bool selected,
            Action<ChannelViewModel, bool> setter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            setter(GetChannel(channelId), selected);
            return ValueTask.CompletedTask;
        }

        private ChannelViewModel GetChannel(ChannelId channelId)
            => channels.TryGetValue(channelId, out ChannelViewModel? channel)
                ? channel
                : throw new KeyNotFoundException($"Unknown channel ID '{channelId}'.");
    }
}
