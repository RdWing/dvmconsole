using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

// Connects the platform-neutral patch router to configured Avalonia channels
// and FNE systems. Patch audio is sourced from channels that are already being
// decoded by the receive coordinator; no hidden audio device is opened.
public sealed class PatchForwardingCoordinator : IDisposable
{
    private static readonly TimeSpan UnavailableDiagnosticInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IReadOnlyList<IFneTrafficEndpoint> systems;
    private readonly PatchMemberResolver memberResolver;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
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
        IEnumerable<IFneTrafficEndpoint> systems,
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Action<PatchForwardingDiagnostic>? diagnosticObserver = null)
    {
        this.systems = systems?.ToArray() ?? throw new ArgumentNullException(nameof(systems));
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.diagnosticObserver = diagnosticObserver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend());
        memberResolver = new PatchMemberResolver(this.systems.SelectMany(system => system.Channels));
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
                ChannelViewModel? channel = memberResolver.Resolve(member);
                if (channel is null)
                {
                    ReportUnavailable(member, member.HasConfiguredChannelIdentity
                        ? "the configured channel was not found"
                        : "the legacy system/talkgroup identity is missing or ambiguous");
                    continue;
                }

                resolvedMembers.Add(PatchMemberResolver.FromChannel(channel));
            }

            resolvedMemberships[groupName] = resolvedMembers;
        }

        router.ApplyMemberships(resolvedMemberships, oneWayModes);
    }

    public void ObserveTraffic(ChannelViewModel source, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.StreamId == 0)
            return;

        if (traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
        {
            router.HandleCallStart(ToAddress(source), traffic.StreamId, traffic.SourceId);
        }
    }

    public void ObserveDecodedSamples(ChannelViewModel source, ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StreamId is uint streamId && source.SourceId is uint sourceId)
            ObserveDecodedSamples(source, streamId, sourceId, samples);
    }

    public void ObserveDecodedSamples(
        ChannelViewModel source,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId != 0 && sourceId != 0)
            router.HandleAudio(ToAddress(source), streamId, sourceId, samples);
    }

    public void StopSource(ChannelViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StreamId is uint streamId)
            StopSource(source, streamId);
    }

    // Callers end forwarding only at an ordered receive boundary or an accepted
    // lifecycle timeout. This operation is idempotent so timeout cleanup may
    // safely follow a confirmed terminator.
    public void StopSource(ChannelViewModel source, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (streamId != 0)
            router.HandleCallEnd(ToAddress(source), streamId);
    }

    public void StopAll()
    {
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>());
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
        ChannelViewModel? channel = memberResolver.Resolve(member);
        if (channel is null)
        {
            ReportUnavailable(member, member.HasConfiguredChannelIdentity
                ? "the configured channel was not found"
                : "the legacy system/talkgroup identity is missing or ambiguous");
            return 0;
        }

        IFneTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
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
        if (!channel.CanTransmit)
        {
            ReportUnavailable(member, "the target channel cannot transmit");
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
            pump = new PatchTransmitPump(session, startAfter);
            session = null;
            var activeTarget = new ActiveTarget(pump);
            lock (sync)
            {
                activeTargets[BuildStreamKey(member, streamId)] = activeTarget;
                targetPumps[member.Key] = pump;
                transmitPumps.Add(pump);
                startingTargets.Remove(member.Key);
                ClearUnavailableDiagnostics(member);
            }
            _ = ObserveTargetStartAsync(
                member,
                streamId,
                sourceId,
                channel.Definition.Mode,
                activeTarget);
            _ = ObserveTargetCompletionAsync(member, streamId, activeTarget);
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
                DateTimeOffset.UtcNow,
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
            Report(new PatchForwardingDiagnostic(
                DateTimeOffset.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch audio failed on {FormatTarget(member)}, stream {streamId}: {exception.Message}",
                exception));
            EndTarget(member, streamId, sourceId);
            router.ReportTargetFailure(member, streamId);
        }
    }

    private uint GetFallbackSourceId(PatchMemberAddress member)
    {
        IFneTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        return system?.SourceId ?? 0;
    }

    private static PatchMemberAddress ToAddress(ChannelViewModel channel)
        => PatchMemberResolver.FromChannel(channel);

    private static string BuildStreamKey(PatchMemberAddress member, uint streamId)
        => $"{member.Key}|{streamId}";

    private void ReportUnavailable(PatchMemberAddress member, string reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
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
            Report(new PatchForwardingDiagnostic(
                DateTimeOffset.UtcNow,
                PatchForwardingDiagnosticKind.TargetFailed,
                member,
                streamId,
                $"Patch target failed on {FormatTarget(member)}, stream {streamId}: {exception.Message}",
                exception));
            router.ReportTargetFailure(member, streamId);
            return;
        }

        Report(new PatchForwardingDiagnostic(
            DateTimeOffset.UtcNow,
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
            DateTimeOffset.UtcNow,
            PatchForwardingDiagnosticKind.TargetStarted,
            member,
            streamId,
            $"Patch target started on {FormatTarget(member)}: " +
            $"{mode.ToUpperInvariant()} source {sourceId}, stream {streamId}."));
    }

    private sealed record ActiveTarget(PatchTransmitPump Pump);
}
