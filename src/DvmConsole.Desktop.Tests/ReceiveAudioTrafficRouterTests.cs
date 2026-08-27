using System.Diagnostics;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Operations;
using Xunit;
using Xunit.Abstractions;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveAudioTrafficRouterTests
{
    private readonly ITestOutputHelper output;

    public ReceiveAudioTrafficRouterTests(ITestOutputHelper output)
        => this.output = output;

    [Fact]
    public void RoutesTarArmedCardWithoutLiveRxAsDecodeTarget()
    {
        ChannelViewModel tarOnly = Channel("TAR only", "100", slot: 1);
        tarOnly.SetRecordingEnabled(true);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [tarOnly]
        };

        ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [tarOnly],
            Traffic(slot: 0),
            (_, _) => false);

        Assert.Same(tarOnly, Assert.Single(targets));
        Assert.False(tarOnly.IsAudioEnabled);
    }

    [Fact]
    public void RoutesDecodedSamplesToTarArmedCopyWhenRxOwnerIsNotArmed()
    {
        ChannelViewModel rxOwner = Channel("Dispatch RX", "100", slot: 1);
        ChannelViewModel tarCopy = Channel("Dispatch TAR", "100", slot: 1);
        tarCopy.SetRecordingEnabled(true);

        ChannelViewModel? target = ReceiveRecordingTargetResolver.Resolve(
            rxOwner,
            [rxOwner, tarCopy]);

        Assert.Same(tarCopy, target);
        Assert.False(rxOwner.IsRecordingEnabled);
    }

    [Fact]
    public void KeepsDecodedTarOwnerWhenMultipleCopiesAreArmed()
    {
        ChannelViewModel decodedOwner = Channel("Dispatch A", "100", slot: 1);
        ChannelViewModel otherCopy = Channel("Dispatch B", "100", slot: 1);
        decodedOwner.SetRecordingEnabled(true);
        otherCopy.SetRecordingEnabled(true);

        ChannelViewModel? target = ReceiveRecordingTargetResolver.Resolve(
            decodedOwner,
            [otherCopy, decodedOwner]);

        Assert.Same(decodedOwner, target);
    }

    [Fact]
    public void DoesNotRedirectRecordingAcrossSystemsOrDmrSlots()
    {
        ChannelViewModel decodedOwner = Channel("Dispatch RX", "100", slot: 1);
        ChannelViewModel otherSystem = Channel(
            "Other system TAR",
            "100",
            slot: 1,
            system: "System 2");
        ChannelViewModel otherSlot = Channel("Slot 2 TAR", "100", slot: 2);
        otherSystem.SetRecordingEnabled(true);
        otherSlot.SetRecordingEnabled(true);

        ChannelViewModel? target = ReceiveRecordingTargetResolver.Resolve(
            decodedOwner,
            [otherSystem, otherSlot]);

        Assert.Null(target);
    }

    [Fact]
    public void RoutesOnlyTheActiveMatchingSlotAndDeduplicatesZoneCopies()
    {
        ChannelViewModel slotOne = Channel("Slot 1", "100", slot: 1);
        ChannelViewModel duplicate = Channel("Slot 1 Copy", "100", slot: 1);
        ChannelViewModel slotTwo = Channel("Slot 2", "100", slot: 2);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [slotOne, duplicate, slotTwo]
        };

        IReadOnlyList<ChannelViewModel> targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [slotOne, duplicate, slotTwo],
            Traffic(slot: 0),
            (_, _) => false);

        Assert.Single(targets);
        Assert.Same(slotOne, targets[0]);
    }

    [Fact]
    public void RoutesDmrPrivacyMetadataThroughTheSameImmutableOwner()
    {
        ChannelViewModel first = Channel("First", "100", slot: 1);
        ChannelViewModel duplicate = Channel("Duplicate", "100", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [first, duplicate]
        };
        FneTrafficFrame privacyHeader = Traffic(
            FneTrafficProtocol.Dmr,
            destinationId: 100,
            slot: 0,
            frameType: "DATA_SYNC",
            subtype: "VOICE_PI_HEADER");

        ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [first, duplicate],
            privacyHeader,
            (_, _) => false);

        Assert.Equal([first], targets);
    }

    [Fact]
    public void RoutesDestinationlessTerminatorOnlyToItsTrackedStream()
    {
        ChannelViewModel first = Channel("First", "100", slot: 1, mode: "p25");
        ChannelViewModel second = Channel("Second", "101", slot: 1, mode: "p25");
        ChannelViewModel dmrCollision = Channel("DMR", "102", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.P25, 100)] = [first],
            [(FneTrafficProtocol.P25, 101)] = [second],
            [(FneTrafficProtocol.Dmr, 102)] = [dmrCollision]
        };
        FneTrafficFrame terminator = new(
            FneTrafficProtocol.P25,
            1,
            0,
            0,
            null,
            "GROUP",
            "TERMINATOR",
            "TDU",
            2,
            77,
            []);

        IReadOnlyList<ChannelViewModel> targets = ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [first, second, dmrCollision],
            terminator,
            (channel, streamId) => ReferenceEquals(channel, second) && streamId == 77);

        Assert.Single(targets);
        Assert.Same(second, targets[0]);
    }

    [Fact]
    public void PresentationRoutingPreservesLegacyOwnerPriorityAndSlotGroupOrder()
    {
        ChannelViewModel slotOne = Channel("Slot 1", "100", slot: 1);
        ChannelViewModel activeCopy = Channel("Active copy", "100", slot: 1);
        ChannelViewModel slotTwo = Channel("Slot 2", "100", slot: 2);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [slotOne, activeCopy, slotTwo]
        };

        ChannelViewModel[] candidates = ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            [slotOne, activeCopy, slotTwo],
            Traffic(slot: 0),
            isAudioActive: channel => ReferenceEquals(channel, activeCopy),
            isPatchActive: _ => false,
            isTrackingStream: (_, _) => false);

        Assert.Equal(2, candidates.Length);
        Assert.Same(activeCopy, candidates[0]);
        Assert.Same(slotTwo, candidates[1]);
    }

    [Fact]
    public void PresentationRoutingPreservesDestinationlessTerminatorFallbackOrder()
    {
        ChannelViewModel routed = Channel("Routed", "100", slot: 1, mode: "p25");
        ChannelViewModel tracked = Channel("Tracked", "101", slot: 1, mode: "p25");
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.P25, 100)] = [routed],
            [(FneTrafficProtocol.P25, 101)] = [tracked]
        };
        FneTrafficFrame terminator = Traffic(
            FneTrafficProtocol.P25,
            destinationId: 0,
            slot: null,
            frameType: "TERMINATOR",
            subtype: "TDU");

        ChannelViewModel[] candidates = ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            [routed, tracked],
            terminator,
            isAudioActive: _ => false,
            isPatchActive: _ => false,
            isTrackingStream: (channel, streamId) =>
                ReferenceEquals(channel, tracked) && streamId == 77);

        Assert.Equal([tracked], candidates);
    }

    [Fact]
    public void PresentationTerminatorRoutingPreservesLegacyReferenceDistinctOrder()
    {
        ChannelViewModel routed = Channel("Routed", "100", slot: 1, mode: "p25");
        ChannelViewModel tracked = Channel("Tracked", "101", slot: 1, mode: "p25");
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.P25, 100)] = [routed, routed],
            [(FneTrafficProtocol.P25, 101)] = [tracked]
        };
        FneTrafficFrame terminator = Traffic(
            FneTrafficProtocol.P25,
            destinationId: 100,
            slot: null,
            frameType: "TERMINATOR",
            subtype: "TDU");
        ChannelViewModel[] systemChannels = [routed, tracked, tracked];

        ChannelViewModel[] candidates = ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            systemChannels,
            terminator,
            isAudioActive: _ => false,
            isPatchActive: _ => false,
            isTrackingStream: (channel, streamId) =>
                ReferenceEquals(channel, tracked) && streamId == 77);

        Assert.Equal([routed, tracked], candidates);
    }

    [Fact]
    public void DelayedPresentationReplaysIngressDecisionWithoutRewindingReducer()
    {
        ChannelViewModel owner = Channel("Owner", "100", slot: 1);
        ChannelViewModel copy = Channel("Copy", "100", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [owner, copy]
        };
        long started = Stopwatch.GetTimestamp();
        long tenMilliseconds = Math.Max(1, Stopwatch.Frequency / 100);
        FneTrafficFrame first = TraceTraffic(0, streamId: 77, timestamp: started);
        FneTrafficFrame second = TraceTraffic(
            1,
            streamId: 77,
            timestamp: started + tenMilliseconds);
        FneTrafficFrame third = TraceTraffic(
            2,
            streamId: 77,
            timestamp: started + (2 * tenMilliseconds));

        ReceiveIngressRoutingDecision firstDecision = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            first,
            (_, _) => false);
        ReceiveIngressRoutingDecision secondDecision = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            second,
            (_, _) => false);
        Assert.True(firstDecision.TryGet(owner.SessionDefinition.RouteKey, out var firstRoute));
        Assert.True(secondDecision.TryGet(owner.SessionDefinition.RouteKey, out var secondRoute));
        Assert.Equal(firstRoute.ActiveStreamIds, secondRoute.ActiveStreamIds);
        Assert.Equal(ReceiveStreamTransition.Started, firstRoute.StreamDecision.Transition);
        Assert.Equal(ReceiveStreamTransition.Continued, secondRoute.StreamDecision.Transition);
        Assert.False(firstDecision.IsContinuationOnly);
        Assert.True(secondDecision.IsContinuationOnly);

        // Simulate an audio/UI backlog presenting packet one only after packet
        // two was already observed at ingress. Replaying the old observation
        // through the reducer here would incorrectly begin episode two.
        Assert.Same(owner, Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            [owner, copy],
            first,
            firstDecision,
            (_, _) => false)));
        Assert.Same(owner, Assert.Single(ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            [owner, copy],
            first,
            firstDecision,
            isAudioActive: _ => false,
            isPatchActive: _ => false,
            isTrackingStream: (_, _) => false)));

        ReceiveIngressRoutingDecision thirdDecision = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            third,
            (_, _) => false);
        Assert.True(thirdDecision.TryGet(owner.SessionDefinition.RouteKey, out var thirdRoute));
        Assert.Contains(77u, thirdRoute.ActiveStreamIds);
        Assert.Equal(ReceiveStreamTransition.Continued, thirdRoute.StreamDecision.Transition);
    }

    [Fact]
    public void DelayedPrivacyProjectionUsesCapturedIngressStreamState()
    {
        ChannelViewModel owner = Channel("Owner", "100", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [owner]
        };
        DateTimeOffset startedAt = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        FneTrafficFrame start = Traffic(
            FneTrafficProtocol.Dmr,
            destinationId: 100,
            slot: 0,
            frameType: "DATA_SYNC",
            subtype: "VOICE_LC_HEADER");
        FneTrafficFrame privacy = Traffic(
            FneTrafficProtocol.Dmr,
            destinationId: 100,
            slot: 0,
            frameType: "DATA_SYNC",
            subtype: "VOICE_PI_HEADER",
            sequence: 2);

        _ = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            start,
            (_, _) => false,
            startedAt);
        ReceiveIngressRoutingDecision privacyIngress =
            ReceiveAudioTrafficRouter.ObserveIngress(
                routes,
                privacy,
                (_, _) => false,
                startedAt.AddMilliseconds(20));

        Assert.False(owner.IsTrackingReceiveStream(privacy.StreamId));
        Assert.True(privacyIngress.TryGet(
            owner.SessionDefinition.RouteKey,
            out ReceiveIngressRouteDecision routeDecision));
        ChannelTrafficApplyResult applied = owner.ApplyTraffic(
            "System 1",
            privacy,
            startedAt.AddMilliseconds(20),
            routeDecision);

        Assert.True(applied.Matched);
        Assert.Equal(ReceiveStreamTransition.Continued, applied.Transition);
        Assert.True(owner.IsTrackingReceiveStream(privacy.StreamId));
    }

    [Fact]
    public void LateTerminatorCannotReviveAStreamExpiredDuringIngressAdvance()
    {
        ChannelViewModel owner = Channel("Owner", "100", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [owner]
        };
        DateTimeOffset startedAt = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        FneTrafficFrame voice = Traffic(slot: 0);
        FneTrafficFrame terminator = Traffic(
            FneTrafficProtocol.Dmr,
            destinationId: 100,
            slot: 0,
            frameType: "TERMINATOR",
            subtype: "TERMINATOR_WITH_LC",
            sequence: 2);

        _ = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            voice,
            (_, _) => false,
            startedAt);
        ReceiveIngressRoutingDecision lateTerminator =
            ReceiveAudioTrafficRouter.ObserveIngress(
                routes,
                terminator,
                (_, _) => false,
                startedAt.AddSeconds(3));

        Assert.True(lateTerminator.TryGet(
            owner.SessionDefinition.RouteKey,
            out ReceiveIngressRouteDecision routeDecision));
        Assert.Contains(
            routeDecision.PrecedingDecisions,
            decision => decision.StreamDecision.Transition ==
                ReceiveStreamTransition.GraceExpired);
        Assert.Equal(
            ReceiveStreamTransition.IgnoredLate,
            routeDecision.StreamDecision.Transition);
        Assert.Empty(routeDecision.ActiveStreamIds);
    }

    [Fact]
    public void TenThousandPacketIngressTraceMatchesIndependentParityOracle()
    {
        const int packetCount = 10_000;
        const int packetsPerEpisode = 100;
        ChannelViewModel owner = Channel("Owner", "100", slot: 1);
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [owner]
        };
        long started = Stopwatch.GetTimestamp();
        long tenMilliseconds = Math.Max(1, Stopwatch.Frequency / 100);
        long fourSeconds = checked(Stopwatch.Frequency * 4);
        var decodeOrder = new List<int>(packetCount);
        var historyOrder = new List<int>(packetCount);
        var tarOrder = new List<int>(packetCount);
        var patchOrder = new List<int>(packetCount);
        var endedStreams = new List<uint>(packetCount / packetsPerEpisode);
        ReceiveAction delivery = ReceiveAction.Present | ReceiveAction.Deliver;

        for (int index = 0; index < packetCount; index++)
        {
            int episodeOffset = index % packetsPerEpisode;
            int episodeIndex = index / packetsPerEpisode;
            uint streamId = (uint)(episodeIndex + 1);
            FneTrafficFrame traffic = TraceTraffic(
                index,
                streamId,
                started + (episodeIndex * fourSeconds) +
                    (episodeOffset * tenMilliseconds),
                definitiveStart: episodeOffset == 0,
                terminator: episodeOffset == packetsPerEpisode - 1);
            ReceiveIngressRoutingDecision ingress = ReceiveAudioTrafficRouter.ObserveIngress(
                routes,
                traffic,
                (_, _) => true);

            Assert.True(ingress.TryGet(owner.SessionDefinition.RouteKey, out var actual));
            foreach (ReceiveRouteProjectionDecision preceding in actual.PrecedingDecisions)
            {
                if (preceding.StreamDecision.EndedStreamId is uint endedStreamId)
                    endedStreams.Add(endedStreamId);
            }
            Assert.Equal(
                episodeOffset == packetsPerEpisode - 1 ? 0u : streamId,
                actual.PrimaryStreamId);
            Assert.Equal(1, actual.StreamCount);
            Assert.Equal(delivery, actual.Actions);
            Assert.Equal(
                episodeOffset == packetsPerEpisode - 1
                    ? ReceiveStreamTransition.TerminationPending
                    : episodeOffset == 0
                        ? ReceiveStreamTransition.Started
                        : ReceiveStreamTransition.Continued,
                actual.StreamDecision.Transition);

            ChannelViewModel decodeOwner = Assert.Single(
                ReceiveAudioTrafficRouter.ResolveTargets(
                    routes,
                    [owner],
                    traffic,
                    ingress,
                    (_, _) => true));
            ChannelViewModel presentationOwner = Assert.Single(
                ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
                    routes,
                    [owner],
                    traffic,
                    ingress,
                    isAudioActive: _ => false,
                    isPatchActive: _ => false,
                    isTrackingStream: (_, _) => true));
            Assert.Same(owner, decodeOwner);
            Assert.Same(owner, presentationOwner);

            if (actual.Actions.HasFlag(ReceiveAction.Deliver))
                decodeOrder.Add(index);
            if (actual.Actions.HasFlag(ReceiveAction.Present))
                historyOrder.Add(index);
            if (actual.Actions.HasFlag(ReceiveAction.Deliver))
                tarOrder.Add(index);
            if (actual.Actions.HasFlag(ReceiveAction.Deliver))
                patchOrder.Add(index);
        }

        int[] oracleOrder = Enumerable.Range(0, packetCount).ToArray();
        Assert.Equal(oracleOrder, decodeOrder);
        Assert.Equal(oracleOrder, historyOrder);
        Assert.Equal(oracleOrder, tarOrder);
        Assert.Equal(oracleOrder, patchOrder);
        IReadOnlyList<ReceiveRouteProjectionDecision> finalAdvance =
            ReceiveAudioTrafficRouter.Advance(
                routes,
                DateTimeOffset.UnixEpoch + Stopwatch.GetElapsedTime(
                    0,
                    started +
                    (((packetCount / packetsPerEpisode) - 1) * fourSeconds) +
                    ((packetsPerEpisode - 1) * tenMilliseconds) +
                    (2 * Stopwatch.Frequency)));
        endedStreams.AddRange(finalAdvance
            .Where(decision => decision.StreamDecision.EndedStreamId is not null)
            .Select(decision => decision.StreamDecision.EndedStreamId!.Value));
        Assert.Equal(
            Enumerable.Range(1, packetCount / packetsPerEpisode).Select(value => (uint)value),
            endedStreams);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void SnapshotPresentationRoutingAllocatesAtLeastEightyPercentLessThanLegacyConstruction(
        int channelCount)
    {
        const int iterations = 512;
        ChannelViewModel[] channels = Enumerable.Range(0, channelCount)
            .Select(index => Channel($"Copy {index:D3}", "100", slot: 1))
            .ToArray();
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = channels
        };
        FneTrafficFrame[] traffic = Enumerable.Range(0, iterations)
            .Select(index => Traffic(slot: 0, sequence: index))
            .ToArray();
        Func<ChannelViewModel, bool> inactive = _ => false;
        Func<ChannelViewModel, uint, bool> untracked = (_, _) => false;

        _ = ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
            routes,
            channels,
            traffic[0],
            inactive,
            inactive,
            untracked);
        _ = ResolveWithLegacyPresentationConstruction(
            channels,
            traffic[0],
            inactive,
            inactive);
        long snapshotBytes = MeasureAllocations(() =>
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                GC.KeepAlive(ReceiveAudioTrafficRouter.ResolvePresentationCandidates(
                    routes,
                    channels,
                    traffic[iteration],
                    inactive,
                    inactive,
                    untracked));
            }
        });
        long legacyBytes = MeasureAllocations(() =>
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                GC.KeepAlive(ResolveWithLegacyPresentationConstruction(
                    channels,
                    traffic[iteration],
                    inactive,
                    inactive));
            }
        });

        double reduction = 1 - (snapshotBytes / (double)legacyBytes);
        output.WriteLine(
            $"{channelCount} channels: snapshot={snapshotBytes:N0} B, " +
            $"legacy={legacyBytes:N0} B, reduction={reduction:P1}");
        Assert.True(
            reduction >= 0.80,
            $"Expected at least 80% fewer steady-state routing allocations for {channelCount} channels; " +
            $"snapshot={snapshotBytes:N0} B, legacy={legacyBytes:N0} B, reduction={reduction:P1}.");
    }

    [Fact]
    public void CommonSingleTargetDispatchRemovesOnePerPacketArrayAcrossTenThousandFrames()
    {
        const int iterations = 10_000;
        ChannelViewModel owner = Channel("Owner", "100", slot: 1);
        ChannelViewModel[] activeChannels = [owner];
        var routes = new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
        {
            [(FneTrafficProtocol.Dmr, 100)] = [owner]
        };
        FneTrafficFrame traffic = Traffic(slot: 0);
        Func<ChannelViewModel, uint, bool> untracked = (_, _) => false;
        ReceiveIngressRoutingDecision ingress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            traffic,
            untracked);

        ReceiveDispatchTargets warmup = ReceiveAudioTrafficRouter.ResolveDispatchTargets(
            routes,
            activeChannels,
            includeRecordingChannels: false,
            traffic,
            ingress,
            untracked);
        Assert.Same(owner, Assert.Single(warmup));

        long dispatchBytes = MeasureAllocations(() =>
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                ReceiveDispatchTargets targets = ReceiveAudioTrafficRouter.ResolveDispatchTargets(
                    routes,
                    activeChannels,
                    includeRecordingChannels: false,
                    traffic,
                    ingress,
                    untracked);
                GC.KeepAlive(targets[0]);
            }
        });
        long legacyBytes = MeasureAllocations(() =>
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                ChannelViewModel[] targets = ReceiveAudioTrafficRouter.ResolveTargets(
                    routes,
                    activeChannels,
                    traffic,
                    ingress,
                    untracked);
                GC.KeepAlive(targets[0]);
            }
        });

        output.WriteLine(
            $"Single-target dispatch: value-set={dispatchBytes:N0} B, " +
            $"array={legacyBytes:N0} B over {iterations:N0} frames.");
        Assert.True(
            legacyBytes - dispatchBytes >= iterations * 24L,
            $"Expected at least one small array allocation removed per frame; " +
            $"value-set={dispatchBytes:N0} B, array={legacyBytes:N0} B.");
    }

    private static ChannelViewModel Channel(
        string name,
        string tgid,
        byte slot,
        string mode = "dmr",
        string system = "System 1")
        => new(new ChannelConfiguration
        {
            Name = name,
            System = system,
            Tgid = tgid,
            Mode = mode,
            Slot = slot
        });

    private static FneTrafficFrame Traffic(byte slot, int sequence = 1)
        => Traffic(
            FneTrafficProtocol.Dmr,
            destinationId: 100,
            slot: slot,
            frameType: "VOICE",
            subtype: "VOICE",
            sequence: sequence);

    private static FneTrafficFrame Traffic(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte? slot,
        string frameType,
        string subtype,
        int sequence = 1)
        => new(
            protocol,
            1,
            2,
            destinationId,
            slot,
            "GROUP",
            frameType,
            subtype,
            checked((ushort)sequence),
            77,
            []);

    private static FneTrafficFrame TraceTraffic(
        int sequence,
        uint streamId,
        long timestamp,
        bool definitiveStart = false,
        bool terminator = false)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: 0,
            callType: "GROUP",
            frameType: terminator
                ? "TERMINATOR"
                : definitiveStart ? "DATA_SYNC" : "VOICE",
            subtype: terminator
                ? "TERMINATOR_WITH_LC"
                : definitiveStart ? "VOICE_LC_HEADER" : "VOICE",
            packetSequence: checked((ushort)sequence),
            streamId,
            payload: [],
            fneBoundaryTimestamp: timestamp);

    // Encodes the removed per-packet GroupBy/Select/ToArray implementation so
    // the allocation threshold guards the actual production migration.
    private static IReadOnlyList<ChannelViewModel> ResolveWithLegacyPresentationConstruction(
        IEnumerable<ChannelViewModel> channels,
        FneTrafficFrame traffic,
        Func<ChannelViewModel, bool> isAudioActive,
        Func<ChannelViewModel, bool> isPatchActive)
        => channels
            .GroupBy(channel => channel.SessionDefinition.RouteKey)
            .Select(group => group.FirstOrDefault(channel =>
                    channel.State == ChannelRuntimeState.Receiving &&
                    channel.StreamId == traffic.StreamId) ??
                group.FirstOrDefault(isAudioActive) ??
                group.FirstOrDefault(isPatchActive) ??
                group.FirstOrDefault(channel => channel.IsRecordingEnabled) ??
                group.First())
            .ToArray();

    private static long MeasureAllocations(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
