// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

public sealed record ReceivePlaybackIdentity(uint SourceId, uint StreamId);

/// <summary>Session-owned receive stream projection, playback identity, and observed encryption.</summary>
public sealed class ChannelReceiveState
{
    private readonly ChannelRuntime runtime;
    private long lastCallerSource = -1;
    internal ChannelReceiveState(ChannelRuntime runtime)
    {
        this.runtime = runtime;
        runtime.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChannelRuntime.State) or nameof(ChannelRuntime.SourceId) &&
                runtime.State == ChannelRuntimeState.Receiving && runtime.SourceId is uint source)
                Volatile.Write(ref lastCallerSource, source);
        };
    }

    public uint? LastCallerSource
    {
        get
        {
            long source = Volatile.Read(ref lastCallerSource);
            return source < 0 ? null : checked((uint)source);
        }
    }
    public event EventHandler? Changed;
    private void PublishChanged()
    {
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* One observer must not hide a receive-state change from others. */ }
        }
    }
    private readonly object playbackSync = new();
    private ReceivePlaybackIdentity? playback;
    private long meterStreamId;
    private long ignoredLatePackets;
    private long droppedFrames;

    public ReceivePlaybackIdentity? Playback => Volatile.Read(ref playback);
    public long MeterStreamId => Interlocked.Read(ref meterStreamId);
    public long IgnoredLatePackets => Interlocked.Read(ref ignoredLatePackets);
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);
    internal void RecordIgnoredLatePacket() => Interlocked.Increment(ref ignoredLatePackets);
    internal void RecordDroppedFrame() => Interlocked.Increment(ref droppedFrames);
    internal void BeginMeter(uint streamId)
    {
        if (streamId != 0) Interlocked.CompareExchange(ref meterStreamId, streamId, 0);
    }
    internal void EndMeter(uint streamId)
    {
        if (streamId != 0) Interlocked.CompareExchange(ref meterStreamId, 0, streamId);
    }
    internal bool TryBeginPlayback(uint sourceId, uint streamId)
    {
        if (streamId == 0) return false;
        lock (playbackSync)
        {
            if (playback is not null) return false;
            Volatile.Write(ref playback, new(sourceId, streamId));
        }
        PublishChanged();
        return true;
    }
    internal bool EndPlayback(uint streamId)
    {
        lock (playbackSync)
        {
            if (playback?.StreamId != streamId) return false;
            Volatile.Write(ref playback, null);
        }
        PublishChanged();
        return true;
    }
    internal void ClearPlayback()
    {
        Interlocked.Exchange(ref meterStreamId, 0);
        bool changed;
        lock (playbackSync)
        {
            changed = playback is not null;
            Volatile.Write(ref playback, null);
        }
        if (changed) PublishChanged();
    }

    private ImmutableHashSet<uint> activeStreams = ImmutableHashSet<uint>.Empty;
    private TrafficEncryptionObservationState encryption = new();
    private uint encryptionStreamId;

    public bool ObservedEncrypted => encryption.Encryption.IsSecure;
    public bool IsTracking(uint streamId) => Volatile.Read(ref activeStreams).Contains(streamId);

    internal bool CanProjectTraffic(string systemName, IRadioMediaFrame traffic, bool tracked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        ArgumentNullException.ThrowIfNull(traffic);
        RadioMediaProtocol expectedProtocol = runtime.Definition.Protocol switch
        {
            ChannelProtocol.Dmr => RadioMediaProtocol.Dmr,
            ChannelProtocol.P25 => RadioMediaProtocol.P25,
            ChannelProtocol.Nxdn => RadioMediaProtocol.Nxdn,
            ChannelProtocol.Analog => RadioMediaProtocol.Analog,
            _ => throw new InvalidOperationException("The channel protocol is unsupported.")
        };
        if (!runtime.Definition.SystemName.Equals(systemName, StringComparison.OrdinalIgnoreCase) ||
            traffic.Protocol != expectedProtocol || traffic.StreamId == 0 || runtime.State == ChannelRuntimeState.Transmitting)
            return false;
        if (RadioReceiveTrafficClassifier.IsTerminator(traffic)) return true;
        if (RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
            return tracked && runtime.Definition.DestinationId == traffic.DestinationId && runtime.Definition.Slot == traffic.Slot;
        if (traffic.DestinationId != runtime.Definition.DestinationId) return false;
        bool voice = RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic) &&
            (runtime.Definition.Protocol != ChannelProtocol.Dmr || traffic.Slot == runtime.Definition.Slot);
        return (voice || RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic)) && traffic.SourceId != 0;
    }

    internal ReceiveStreamDecision? ApplyProjection(IRadioMediaFrame traffic, DateTimeOffset now,
        ReceiveRouteProjectionDecision projection)
    {
        SetProjection(projection.ActiveStreamIds);
        ReceiveStreamDecision decision = projection.StreamDecision;

        if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
        {
            if (decision.Transition != ReceiveStreamTransition.TerminationPending)
                return decision.Transition == ReceiveStreamTransition.IgnoredLate
                    ? decision
                    : null;

            if (runtime.StreamId == traffic.StreamId)
                runtime.MarkIdle(now);
            return decision;
        }

        if (RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
        {
            if (decision.Transition is (ReceiveStreamTransition.Continued or ReceiveStreamTransition.Resumed) &&
                runtime.StreamId == traffic.StreamId)
            {
                runtime.MarkReceiving(traffic.SourceId, traffic.StreamId, now);
            }
            return decision;
        }

        if (decision.Transition != ReceiveStreamTransition.IgnoredLate &&
            (decision.Transition != ReceiveStreamTransition.Colliding ||
             runtime.State != ChannelRuntimeState.Receiving))
        {
            runtime.MarkReceiving(traffic.SourceId, traffic.StreamId, now);
        }
        return decision;
    }

    internal void SetProjection(ImmutableHashSet<uint> streamIds)
        => Volatile.Write(ref activeStreams, streamIds);

    internal (bool Reset, bool Changed) ObserveEncryption(IRadioMediaFrame traffic)
    {
        bool reset = !RadioReceiveTrafficClassifier.IsTerminator(traffic) && encryptionStreamId != traffic.StreamId;
        if (reset)
        {
            encryptionStreamId = traffic.StreamId;
            encryption = new TrafficEncryptionObservationState();
        }
        bool changed = encryption.Observe(traffic);
        if (reset || changed) PublishChanged();
        return (reset, changed);
    }
}
