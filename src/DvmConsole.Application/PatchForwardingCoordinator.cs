using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

// Connects the platform-neutral patch router to immutable channel descriptors
// and radio endpoints. Patch audio is sourced from channels that are already
// decoded by the receive coordinator; no hidden audio device is opened.
public sealed class PatchForwardingCoordinator : IDisposable
{
    private static readonly TimeSpan UnavailableDiagnosticInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IReadOnlyList<IRadioTrafficEndpoint> systems;
    private readonly PatchTransmitChannelResolver memberResolver;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly IClock clock;
    private readonly TimeProvider timeProvider;
    private readonly Action<PatchForwardingDiagnostic>? diagnosticObserver;
    private readonly Dictionary<string, ActiveTarget> activeTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PatchTransmitPump> targetPumps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> startingTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PatchTransmitPump> transmitPumps = [];
    private readonly Dictionary<string, DateTimeOffset> unavailableDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly PatchRoutingTable router;
    private IVocoderBackend? vocoderBackend;
    private bool disposed;

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
        memberResolver = new PatchTransmitChannelResolver(
            this.systems.SelectMany(system => system.ChannelDescriptors),
            resolveCurrentChannel);
        router = new PatchRoutingTable(BeginTarget, EndTarget, SendAudio, GetFallbackSourceId);
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
            pumps = targetPumps.Values.Distinct().ToArray();
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
        TransmitChannelDescriptor? source = memberResolver.Resolve(sourceId);
        if (source is null)
            return;
        if (traffic.StreamId == 0)
            return;

        if (traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
        {
            router.HandleCallStart(ToAddress(source), traffic.StreamId, traffic.SourceId);
        }
    }

    public void ObserveDecodedSamples(
        ChannelId sourceChannelId,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        TransmitChannelDescriptor? source = memberResolver.Resolve(sourceChannelId);
        if (source is null)
            return;
        if (streamId != 0 && sourceId != 0)
            router.HandleAudio(ToAddress(source), streamId, sourceId, samples);
    }

    // Callers end forwarding only at an ordered receive boundary or an accepted
    // lifecycle timeout. This operation is idempotent so timeout cleanup may
    // safely follow a confirmed terminator.
    public void StopSource(ChannelId sourceChannelId, uint streamId)
    {
        TransmitChannelDescriptor? source = memberResolver.Resolve(sourceChannelId);
        if (source is null)
            return;
        if (streamId != 0)
            router.HandleCallEnd(ToAddress(source), streamId);
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
        if (disposed)
            return;

        StopAll();
        PatchTransmitPump[] pumps;
        lock (sync)
        {
            pumps = transmitPumps.ToArray();
            foreach (PatchTransmitPump pump in pumps)
                pump.Complete();
            activeTargets.Clear();
            targetPumps.Clear();
            startingTargets.Clear();
            unavailableDiagnostics.Clear();
        }
        Task.WhenAll(pumps.Select(pump => pump.Completion)).GetAwaiter().GetResult();
        lock (sync)
        {
            transmitPumps.Clear();
            vocoderBackend?.Dispose();
            vocoderBackend = null;
            disposed = true;
        }
    }

    private uint BeginTarget(PatchMemberAddress member, uint sourceId)
    {
        if (disposed)
        {
            ReportUnavailable(member, "the patch coordinator is stopping");
            return 0;
        }
        TransmitChannelDescriptor? channel = memberResolver.Resolve(member);
        if (channel is null)
        {
            ReportUnavailable(member, member.HasConfiguredChannelIdentity
                ? "the configured channel was not found"
                : "the legacy system/talkgroup identity is missing or ambiguous");
            return 0;
        }

        IRadioTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        if (system is null)
        {
            ReportUnavailable(member, "the configured FNE system was not found");
            return 0;
        }
        if (!system.IsConnected)
        {
            ReportUnavailable(member, "the target FNE is disconnected");
            return 0;
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
            return 0;
        }
        if (sourceId == 0)
        {
            ReportUnavailable(member, "the target FNE has no usable source ID");
            return 0;
        }

        string? unavailableReason = null;
        Task? startAfter = null;
        lock (sync)
        {
            if (targetPumps.TryGetValue(member.Key, out PatchTransmitPump? existingPump))
            {
                if (!existingPump.Completion.IsCompleted)
                {
                    bool existingTargetIsActive = activeTargets.Values.Any(target =>
                        ReferenceEquals(target.Pump, existingPump));
                    if (existingTargetIsActive)
                        unavailableReason = "the target is already active in another patch route";
                    else
                        startAfter = existingPump.Completion;
                }
                else
                {
                    targetPumps.Remove(member.Key);
                }
            }
            if (unavailableReason is null && !startingTargets.Add(member.Key))
            {
                unavailableReason = "another target call is starting";
            }
        }
        if (unavailableReason is not null)
        {
            ReportUnavailable(member, unavailableReason);
            return 0;
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
                    payload.Span,
                    sequence,
                    stream),
                encryption,
                dmrPrivacy,
                nxdnPrivacy);
            createdVocoderSession = null;
            pump = new PatchTransmitPump(session, startAfter, timeProvider: timeProvider);
            session = null;
            var activeTarget = new ActiveTarget(member, channel.Id, streamId, pump);
            lock (sync)
            {
                activeTargets[BuildStreamKey(member, streamId)] = activeTarget;
                targetPumps[member.Key] = pump;
                transmitPumps.Add(pump);
                startingTargets.Remove(member.Key);
                ClearUnavailableDiagnostics(member);
            }
            ObserveBackground(ObserveTargetStartAsync(
                member,
                streamId,
                sourceId,
                channel.Definition.Mode,
                activeTarget));
            ObserveBackground(ObserveTargetCompletionAsync(member, streamId, activeTarget));
            return streamId;
        }
        catch (Exception exception)
        {
            pump?.Complete();
            session?.Dispose();
            createdVocoderSession?.Dispose();
            lock (sync)
                startingTargets.Remove(member.Key);
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch target could not start on {FormatTarget(member)}: {exception.Message}",
                exception));
            return 0;
        }
    }

    private void EndTarget(PatchMemberAddress member, uint streamId, uint _)
    {
        ActiveTarget? target;
        lock (sync)
        {
            if (!activeTargets.Remove(BuildStreamKey(member, streamId), out target))
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
            activeTargets.TryGetValue(BuildStreamKey(member, streamId), out target);
        if (target is null)
            return;

        try
        {
            target.Pump.Enqueue(samples.Span);
        }
        catch (Exception exception)
        {
            EndTarget(member, streamId, sourceId);
            router.ReportTargetFailure(member, streamId);
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

    private static PatchMemberAddress ToAddress(TransmitChannelDescriptor channel)
        => PatchTransmitChannelResolver.FromChannel(channel);

    private static string BuildStreamKey(PatchMemberAddress member, uint streamId)
        => $"{member.Key}|{streamId}";

    private void ReportUnavailable(PatchMemberAddress member, string reason)
    {
        DateTimeOffset now = clock.UtcNow;
        string key = $"{member.Key}|{reason}";
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
        string prefix = $"{member.Key}|";
        foreach (string key in unavailableDiagnostics.Keys
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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
            string key = BuildStreamKey(member, streamId);
            if (activeTargets.TryGetValue(key, out ActiveTarget? active) && ReferenceEquals(active, target))
                activeTargets.Remove(key);
            if (targetPumps.TryGetValue(member.Key, out PatchTransmitPump? memberPump) &&
                ReferenceEquals(memberPump, target.Pump))
            {
                targetPumps.Remove(member.Key);
            }
            transmitPumps.Remove(target.Pump);
        }

        if (target.Pump.Failure is Exception exception)
        {
            router.ReportTargetFailure(member, streamId);
            Report(new PatchForwardingDiagnostic(
                clock.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
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
