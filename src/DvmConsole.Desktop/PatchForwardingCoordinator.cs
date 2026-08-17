using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.P25;

namespace DvmConsole.Desktop;

// Connects the platform-neutral patch router to configured Avalonia channels
// and FNE systems. Patch audio is sourced from channels that are already being
// decoded by the receive coordinator; no hidden audio device is opened.
public sealed class PatchForwardingCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly IReadOnlyList<IFneTrafficEndpoint> systems;
    private readonly Dictionary<string, ChannelViewModel> channels;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly Dictionary<string, ActiveTarget> activeTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly PatchRoutingTable router;
    private IVocoderBackend? vocoderBackend;
    private bool disposed;

    public PatchForwardingCoordinator(
        IEnumerable<IFneTrafficEndpoint> systems,
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.systems = systems?.ToArray() ?? throw new ArgumentNullException(nameof(systems));
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend());
        channels = this.systems
            .SelectMany(system => system.Channels)
            .GroupBy(channel => BuildKey(channel.Definition.SystemName, channel.Definition.DestinationId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        router = new PatchRoutingTable(BeginTarget, EndTarget, SendAudio, GetFallbackSourceId);
    }

    public bool SourceIdPassthrough
    {
        get => router.SourceIdPassthrough;
        set => router.SourceIdPassthrough = value;
    }

    public IReadOnlyList<string> GroupNames => router.GroupNames;

    public void ApplyMemberships(
        IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool>? oneWayModes = null)
        => router.ApplyMemberships(memberships, oneWayModes);

    public void ObserveTraffic(ChannelViewModel source, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.StreamId == 0)
            return;

        PatchMemberAddress address = ToAddress(source);
        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
        {
            router.HandleCallEnd(address, traffic.StreamId);
            return;
        }

        if (traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
            traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
        {
            router.HandleCallStart(address, traffic.StreamId, traffic.SourceId);
        }
    }

    public void ObserveDecodedSamples(ChannelViewModel source, ReadOnlyMemory<short> samples)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StreamId is uint streamId && source.SourceId is uint sourceId)
            router.HandleAudio(ToAddress(source), streamId, sourceId, samples);
    }

    public void StopSource(ChannelViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.StreamId is uint streamId)
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
        lock (sync)
        {
            foreach (ActiveTarget target in activeTargets.Values.ToArray())
                target.Session.Dispose();
            activeTargets.Clear();
            vocoderBackend?.Dispose();
            vocoderBackend = null;
            disposed = true;
        }
    }

    private uint BeginTarget(PatchMemberAddress member, uint sourceId)
    {
        if (disposed || !channels.TryGetValue(member.Key, out ChannelViewModel? channel))
            return 0;

        IFneTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        if (system is null || !system.IsConnected || !channel.CanTransmit || sourceId == 0)
            return 0;

        uint streamId;
        IVocoderSession? createdVocoderSession = null;
        PatchTransmitSession? session = null;
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
            createdVocoderSession = transmitDefinition.Mode is "dmr" or "p25" or "nxdn"
                ? (vocoderBackend ??= createVocoderBackend()).CreateSession(
                    transmitDefinition.Mode == "dmr"
                        ? VocoderMode.DmrAmbe
                        : transmitDefinition.Mode == "nxdn"
                            ? VocoderMode.NxdnAmbe
                            : VocoderMode.P25Imbe)
                : null;
            session = new PatchTransmitSession(
                transmitDefinition,
                sourceId,
                streamId,
                createdVocoderSession,
                (payload, sequence, stream) => system.SendTraffic(
                    ToProtocol(transmitDefinition.Mode),
                    payload.Span,
                    sequence,
                    stream),
                encryption,
                dmrPrivacy,
                nxdnPrivacy);
            createdVocoderSession = null;
            session.Start();
            lock (sync)
                activeTargets[BuildStreamKey(member, streamId)] = new ActiveTarget(session, channel, system);
            return streamId;
        }
        catch
        {
            session?.Dispose();
            createdVocoderSession?.Dispose();
            return 0;
        }
    }

    private void EndTarget(PatchMemberAddress member, uint streamId, uint sourceId)
    {
        ActiveTarget? target;
        lock (sync)
        {
            if (!activeTargets.Remove(BuildStreamKey(member, streamId), out target))
                return;
        }

        try
        {
            if (target.Session.IsStarted && !target.Session.IsEnded)
                target.Session.End();
        }
        catch
        {
            // A disconnected target is already unusable; disposal below still
            // releases its encoder and any native state.
        }
        finally
        {
            target.Session.Dispose();
        }
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
            target.Session.Process(samples.Span);
        }
        catch
        {
            EndTarget(member, streamId, sourceId);
        }
    }

    private uint GetFallbackSourceId(PatchMemberAddress member)
    {
        IFneTrafficEndpoint? system = systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(member.SystemName, StringComparison.OrdinalIgnoreCase));
        return system?.SourceId ?? 0;
    }

    private static PatchMemberAddress ToAddress(ChannelViewModel channel)
        => new(channel.Definition.SystemName, channel.Definition.DestinationId);

    private static FneTrafficProtocol ToProtocol(string mode)
        => mode switch
        {
            "dmr" => FneTrafficProtocol.Dmr,
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            "analog" => FneTrafficProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static string BuildKey(string systemName, uint destinationId)
        => $"{systemName.ToLowerInvariant()}|{destinationId}";

    private static string BuildStreamKey(PatchMemberAddress member, uint streamId)
        => $"{member.Key}|{streamId}";

    private sealed record ActiveTarget(PatchTransmitSession Session, ChannelViewModel Channel, IFneTrafficEndpoint System);
}
