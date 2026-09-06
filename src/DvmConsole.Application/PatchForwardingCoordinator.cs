// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using System.Collections.Concurrent;

namespace DvmConsole.Application;

// Connects the platform-neutral patch router to immutable channel descriptors
// and radio endpoints. Patch audio is sourced from channels that are already
// decoded by the receive coordinator; no hidden audio device is opened.
public sealed class PatchForwardingCoordinator : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan UnavailableDiagnosticInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SynchronousDisposeWait = TimeSpan.FromMilliseconds(250);

    private readonly object sync = new();
    private readonly IReadOnlyList<IRadioTrafficEndpoint> systems;
    private readonly PatchTransmitChannelResolver memberResolver;
    private readonly ConcurrentDictionary<ChannelId, PatchMemberAddress> memberAddresses = [];
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly IClock clock;
    private readonly TimeProvider timeProvider;
    private readonly Action<PatchForwardingDiagnostic>? diagnosticObserver;
    private readonly Dictionary<TargetStreamKey, ActiveTarget> activeTargets = [];
    // Retain every retiring call. A canceled waiting call must never let its
    // successor bypass a predecessor that is still finishing its wire tail.
    private readonly Dictionary<PatchMemberIdentity, HashSet<PatchTransmitPump>> destinationPumps = [];
    internal const int MaximumCallsPerDestination = 2;
    internal static readonly TimeSpan MaximumQueuedAudioAge = TimeSpan.FromSeconds(1);
    private readonly HashSet<PatchMemberIdentity> startingTargets = [];
    private readonly HashSet<PatchTransmitPump> transmitPumps = [];
    private readonly Dictionary<UnavailableDiagnosticKey, DateTimeOffset> unavailableDiagnostics = [];
    private readonly PatchRoutingTable router;
    private IVocoderBackend? vocoderBackend;
    private volatile bool disposed;
    private Task? disposeTask;

    public PatchForwardingCoordinator(
        IEnumerable<IRadioTrafficEndpoint> systems,
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Action<PatchForwardingDiagnostic>? diagnosticObserver = null,
        Func<ChannelId, TransmitChannelDescriptor?>? resolveCurrentChannel = null,
        IClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        this.systems = systems?.ToArray() ?? throw new ArgumentNullException(nameof(systems));
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.diagnosticObserver = diagnosticObserver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => throw new InvalidOperationException(
                "A vocoder backend factory is required for digital patch targets."));
        this.clock = clock ?? SystemClock.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        TransmitChannelDescriptor[] configuredChannels = this.systems
            .SelectMany(system => system.ChannelDescriptors)
            .ToArray();
        memberResolver = new PatchTransmitChannelResolver(configuredChannels, resolveCurrentChannel);
        foreach (TransmitChannelDescriptor channel in configuredChannels)
            memberAddresses[channel.Id] = PatchTransmitChannelResolver.FromChannel(channel);
        router = PatchRoutingTable.WithAdmission(BeginTarget, EndTarget, SendAudio, GetFallbackSourceId);
    }

    public bool SourceIdPassthrough
    {
        get => router.SourceIdPassthrough;
        set => router.SourceIdPassthrough = value;
    }

    internal TransmitQueueHealth CaptureQueueHealth()
    {
        PatchTransmitPump[] pumps;
        lock (sync)
            pumps = transmitPumps.ToArray();
        if (pumps.Length == 0)
            return default;

        TransmitQueueHealth[] health = pumps
            .Select(pump => pump.CaptureHealth())
            .ToArray();
        return new TransmitQueueHealth(
            health.Sum(entry => entry.Depth),
            health.Sum(entry => entry.PeakDepth),
            health.Max(entry => entry.OldestAge),
            health.Sum(entry => entry.Capacity));
    }

    public IReadOnlyList<string> GroupNames => router.GroupNames;

    public void ApplyMemberships(
        IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool>? oneWayModes = null)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        var resolvedMemberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string groupName, IReadOnlyList<PatchMemberAddress> members) in memberships)
        {
            var resolvedMembers = new List<PatchMemberAddress>();
            foreach (PatchMemberAddress member in members ?? [])
            {
                TransmitChannelDescriptor? channel = memberResolver.Resolve(member);
                if (channel is null)
                {
                    ReportUnavailable(member, member.HasConfiguredChannelIdentity
                        ? "the configured channel was not found"
                        : "the legacy system/talkgroup identity is missing or ambiguous");
                    continue;
                }

                resolvedMembers.Add(PatchTransmitChannelResolver.FromChannel(channel));
            }

            resolvedMemberships[groupName] = resolvedMembers;
        }

        router.ApplyMemberships(resolvedMemberships, oneWayModes);
    }

    public void ObserveTraffic(ChannelId sourceId, IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        PatchMemberAddress? source = ResolveAddress(sourceId);
        if (source is null)
            return;
        if (traffic.StreamId == 0)
            return;

        if (traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
        {
            router.HandleCallStart(source, traffic.StreamId, traffic.SourceId);
        }
    }

    public void ObserveDecodedSamples(
        ChannelId sourceChannelId,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        PatchMemberAddress? source = ResolveAddress(sourceChannelId);
        if (source is null)
            return;
        if (streamId != 0 && sourceId != 0)
            router.HandleAudio(source, streamId, sourceId, samples);
    }

    // Callers end forwarding only at an ordered receive boundary or an accepted
    // lifecycle timeout. This operation is idempotent so timeout cleanup may
    // safely follow a confirmed terminator.
    public void StopSource(ChannelId sourceChannelId, uint streamId)
    {
        PatchMemberAddress? source = ResolveAddress(sourceChannelId);
        if (source is null)
            return;
        if (streamId != 0)
            router.HandleCallEnd(source, streamId);
    }

    public void StopAll()
    {
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>());
    }

    internal int StopUnavailableTargets(IReadOnlyCollection<ChannelId> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        if (channelIds.Count == 0)
            return 0;

        HashSet<ChannelId> unavailable = channelIds.ToHashSet();
        ActiveTarget[] targets;
        lock (sync)
        {
            targets = activeTargets.Values
                .Where(target => unavailable.Contains(target.ChannelId))
                .Distinct()
                .ToArray();
        }

        foreach (ActiveTarget target in targets)
        {
            router.ReportTargetFailure(target.Member, target.StreamId);
            target.Pump.Complete();
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetUnavailable,
                target.Member,
                target.StreamId,
                $"Patch target stopped on {FormatTarget(target.Member)}, stream {target.StreamId}: " +
                "the authoritative FNE talkgroup table no longer permits this target."));
        }

        return targets.Length;
    }

    public void Dispose()
    {
        Task cleanup = BeginDispose();
        if (!cleanup.Wait(SynchronousDisposeWait))
        {
            ObserveBackground(cleanup);
            return;
        }
        cleanup.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
        => new(BeginDispose());

    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
        => await BeginDispose().WaitAsync(cancellationToken).ConfigureAwait(false);

    private Task BeginDispose()
    {
        lock (sync)
            return disposeTask ??= DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        PatchTransmitPump[] pumps;
        IVocoderBackend? ownedVocoder;
        lock (sync)
        {
            disposed = true;
            pumps = transmitPumps.ToArray();
            ownedVocoder = vocoderBackend;
            vocoderBackend = null;
        }

        List<Exception>? failures = null;
        try
        {
            StopAll();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        foreach (PatchTransmitPump pump in pumps)
        {
            try
            {
                pump.DiscardPendingAudioAndComplete();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        lock (sync)
        {
            activeTargets.Clear();
            destinationPumps.Clear();
            startingTargets.Clear();
            unavailableDiagnostics.Clear();
        }

        // Observe every destination independently so one fault cannot leave
        // a later pump or its transmit session unobserved.
        foreach (PatchTransmitPump pump in pumps)
        {
            try
            {
                await pump.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        lock (sync)
            transmitPumps.Clear();
        try
        {
            ownedVocoder?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("One or more patch resources failed to stop.", failures);
    }

    private PatchCallStartResult BeginTarget(PatchMemberAddress member, uint sourceId)
    {
        if (disposed)
        {
            ReportUnavailable(member, "the patch coordinator is stopping");
            return default;
        }
        TransmitChannelDescriptor? channel = memberResolver.Resolve(member);
        if (channel is null)
        {
            ReportUnavailable(member, member.HasConfiguredChannelIdentity
                ? "the configured channel was not found"
                : "the legacy system/talkgroup identity is missing or ambiguous");
            return default;
        }

        IRadioTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        if (system is null)
        {
            ReportUnavailable(member, "the configured FNE system was not found");
            return default;
        }
        if (!system.IsConnected)
        {
            ReportUnavailable(member, "the target FNE is disconnected");
            return default;
        }
        TargetAuthorityState availability =
            TransmitTargetPolicy.GetTalkgroupAvailability(channel, system);
        if (!channel.CanTransmitByConfiguration ||
            availability == TargetAuthorityState.Unavailable)
        {
            string reason = availability == TargetAuthorityState.Unavailable
                ? channel.AuthorityUnavailableReason
                : channel.ConfigurationUnavailableReason;
            ReportUnavailable(member, reason);
            return default;
        }
        if (sourceId == 0)
        {
            ReportUnavailable(member, "the target FNE has no usable source ID");
            return default;
        }

        string? unavailableReason = null;
        Task? startAfter = null;
        bool backlogFull = false;
        lock (sync)
        {
            if (disposed)
                unavailableReason = "the patch coordinator is stopping";
            else if (destinationPumps.TryGetValue(member.Identity, out HashSet<PatchTransmitPump>? owned))
            {
                PatchTransmitPump[] pending = owned.Where(pump => !pump.Completion.IsCompleted).ToArray();
                if (activeTargets.Values.Any(target => target.Member.Identity == member.Identity &&
                    !target.Pump.Completion.IsCompleted))
                    unavailableReason = "the target is already active in another patch route";
                else if (pending.Length >= MaximumCallsPerDestination)
                {
                    backlogFull = true;
                    unavailableReason = "the destination backlog is full; this incoming patch call was skipped";
                }
                else if (pending.Length > 0)
                    startAfter = Task.WhenAll(pending.Select(pump => pump.Completion));
            }
            if (unavailableReason is null && !startingTargets.Add(member.Identity))
            {
                unavailableReason = "another target call is starting";
            }
        }
        if (backlogFull)
        {
            Report(new PatchForwardingDiagnostic(clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetOverloaded, member, 0,
                $"Patch call skipped on {FormatTarget(member)}: {unavailableReason}."));
            return PatchCallStartResult.Skipped;
        }
        if (unavailableReason is not null)
        {
            ReportUnavailable(member, unavailableReason);
            return default;
        }

        uint streamId = 0;
        IVocoderSession? createdVocoderSession = null;
        PatchTransmitSession? session = null;
        PatchTransmitPump? pump = null;
        try
        {
            streamId = system.CreateStreamId();
            ChannelRuntimeDefinition transmitDefinition = ChannelTransmitDefinitionFactory.Create(channel);
            P25TxEncryptionOptions? encryption = ChannelTransmitDefinitionFactory.CreateEncryptionOptions(
                channel,
                transmitDefinition,
                p25KeyResolver);
            DmrPrivacyOptions? dmrPrivacy = ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(
                channel,
                transmitDefinition,
                dmrKeyResolver);
            NxdnPrivacyOptions? nxdnPrivacy = ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(
                channel,
                transmitDefinition,
                nxdnKeyResolver);
            createdVocoderSession = ChannelProtocolMediaMapper.RequiresVocoder(transmitDefinition.Protocol)
                ? (vocoderBackend ??= createVocoderBackend()).CreateSession(
                    ChannelProtocolMediaMapper.ToVocoderMode(transmitDefinition.Protocol))
                : null;
            session = new PatchTransmitSession(
                transmitDefinition,
                sourceId,
                streamId,
                createdVocoderSession,
                (payload, sequence, stream) => system.SendTraffic(
                    ChannelProtocolMediaMapper.ToTrafficProtocol(transmitDefinition.Protocol),
                    payload,
                    sequence,
                    stream),
                encryption,
                dmrPrivacy,
                nxdnPrivacy);
            createdVocoderSession = null;
            pump = new PatchTransmitPump(session, startAfter, timeProvider: timeProvider,
                maximumQueuedAge: MaximumQueuedAudioAge);
            session = null;
            var activeTarget = new ActiveTarget(member, channel.Id, streamId, pump);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                activeTargets[new TargetStreamKey(member.Identity, streamId)] = activeTarget;
                if (!destinationPumps.TryGetValue(member.Identity, out HashSet<PatchTransmitPump>? owned))
                    destinationPumps.Add(member.Identity, owned = []);
                owned.Add(pump);
                transmitPumps.Add(pump);
                startingTargets.Remove(member.Identity);
                ClearUnavailableDiagnostics(member);
            }
            ObserveBackground(ObserveTargetStartAsync(
                member,
                streamId,
                sourceId,
                channel.Definition.Mode,
                activeTarget));
            ObserveBackground(ObserveTargetCompletionAsync(member, streamId, activeTarget));
            return new PatchCallStartResult(streamId);
        }
        catch (Exception exception)
        {
            pump?.Complete();
            session?.Dispose();
            createdVocoderSession?.Dispose();
            lock (sync)
                startingTargets.Remove(member.Identity);
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch target could not start on {FormatTarget(member)}: {exception.Message}",
                exception));
            return default;
        }
    }

    private void EndTarget(PatchMemberAddress member, uint streamId, uint _)
    {
        ActiveTarget? target;
        lock (sync)
        {
            if (!activeTargets.Remove(new TargetStreamKey(member.Identity, streamId), out target))
                return;
        }

        target.Pump.Complete();
    }

    private void SendAudio(
        PatchMemberAddress member,
        uint streamId,
        ReadOnlyMemory<short> samples,
        uint sourceId)
    {
        ActiveTarget? target;
        lock (sync)
            activeTargets.TryGetValue(new TargetStreamKey(member.Identity, streamId), out target);
        if (target is null)
            return;

        try
        {
            target.Pump.Enqueue(samples.Span);
        }
        catch (Exception exception)
        {
            EndTarget(member, streamId, sourceId);
            router.ReportTargetFailure(member, streamId, skipSourceCall: target.Pump.WasOverloaded);
            // A pump-owned fault is reported once by its completion observer.
            if (ReferenceEquals(target.Pump.Failure, exception))
                return;
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch audio failed on {FormatTarget(member)}, stream {streamId}: {exception.Message}",
                exception));
        }
    }

    private uint GetFallbackSourceId(PatchMemberAddress member)
    {
        IRadioTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        return system?.SourceId ?? 0;
    }

    private PatchMemberAddress? ResolveAddress(ChannelId channelId)
    {
        if (memberAddresses.TryGetValue(channelId, out PatchMemberAddress? address))
            return address;

        TransmitChannelDescriptor? channel = memberResolver.Resolve(channelId);
        return channel is null
            ? null
            : memberAddresses.GetOrAdd(
                channelId,
                _ => PatchTransmitChannelResolver.FromChannel(channel));
    }

    private void ReportUnavailable(PatchMemberAddress member, string reason)
    {
        DateTimeOffset now = clock.UtcNow;
        var key = new UnavailableDiagnosticKey(member.Identity, reason);
        lock (sync)
        {
            if (unavailableDiagnostics.TryGetValue(key, out DateTimeOffset lastReported) &&
                now - lastReported < UnavailableDiagnosticInterval)
            {
                return;
            }
            unavailableDiagnostics[key] = now;
        }

        Report(new PatchForwardingDiagnostic(
            now,
            PatchForwardingDiagnosticKind.TargetUnavailable,
            member,
            StreamId: 0,
            $"Patch target unavailable on {FormatTarget(member)}: {reason}."));
    }

    private void ClearUnavailableDiagnostics(PatchMemberAddress member)
    {
        foreach (UnavailableDiagnosticKey key in unavailableDiagnostics.Keys
            .Where(candidate => candidate.Member == member.Identity)
            .ToArray())
        {
            unavailableDiagnostics.Remove(key);
        }
    }

    private void Report(PatchForwardingDiagnostic diagnostic)
    {
        try
        {
            diagnosticObserver?.Invoke(diagnostic);
        }
        catch
        {
            // Diagnostics must never interrupt patch call handling.
        }
    }

    private static string FormatTarget(PatchMemberAddress member)
        => $"{member.SystemName} / TGID {member.DestinationId}";

    private async Task ObserveTargetCompletionAsync(
        PatchMemberAddress member,
        uint streamId,
        ActiveTarget target)
    {
        await target.Pump.Completion.ConfigureAwait(false);
        lock (sync)
        {
            var key = new TargetStreamKey(member.Identity, streamId);
            if (activeTargets.TryGetValue(key, out ActiveTarget? active) && ReferenceEquals(active, target))
                activeTargets.Remove(key);
            if (destinationPumps.TryGetValue(member.Identity, out HashSet<PatchTransmitPump>? owned))
            {
                owned.Remove(target.Pump);
                if (owned.Count == 0)
                    destinationPumps.Remove(member.Identity);
            }
            transmitPumps.Remove(target.Pump);
        }

        if (target.Pump.Failure is Exception exception)
        {
            // Overload ends forwarding for this entire source call. Release
            // transmit/echo state, but suppress a mid-call restart.
            router.ReportTargetFailure(member, streamId, skipSourceCall: target.Pump.WasOverloaded);
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                exception is PatchBacklogException
                    ? PatchForwardingDiagnosticKind.TargetOverloaded : PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch target failed on {FormatTarget(member)}, stream {streamId}: {exception.Message}",
                exception));
            return;
        }

        Report(new PatchForwardingDiagnostic(
            clock.UtcNow,
            PatchForwardingDiagnosticKind.TargetEnded,
            member,
            streamId,
            $"Patch target ended on {FormatTarget(member)}: stream {streamId}."));
    }

    private async Task ObserveTargetStartAsync(
        PatchMemberAddress member,
        uint streamId,
        uint sourceId,
        string mode,
        ActiveTarget target)
    {
        if (!await target.Pump.Started.ConfigureAwait(false))
            return;

        Report(new PatchForwardingDiagnostic(
            clock.UtcNow,
            PatchForwardingDiagnosticKind.TargetStarted,
            member,
            streamId,
            $"Patch target started on {FormatTarget(member)}: " +
            $"{mode.ToUpperInvariant()} source {sourceId}, stream {streamId}."));
    }

    private sealed record ActiveTarget(
        PatchMemberAddress Member,
        ChannelId ChannelId,
        uint StreamId,
        PatchTransmitPump Pump);

    private readonly record struct TargetStreamKey(PatchMemberIdentity Member, uint StreamId);
    private readonly record struct UnavailableDiagnosticKey(PatchMemberIdentity Member, string Reason);

    private static void ObserveBackground(Task task)
        => _ = ObserveBackgroundAsync(task);

    private static async Task ObserveBackgroundAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Patch teardown may cancel an in-flight target observer.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Patch forwarding background task failed: {0}",
                exception);
        }
    }
}
