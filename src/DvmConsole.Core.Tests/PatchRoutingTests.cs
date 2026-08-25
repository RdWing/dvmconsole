using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class PatchRoutingTests
{
    [Fact]
    public void ForwardsCallsToOtherMembersAndSuppressesOutboundEcho()
    {
        PatchMemberAddress source = new("Alpha", 100);
        PatchMemberAddress target = new("Beta", 200);
        var starts = new List<(PatchMemberAddress Member, uint SourceId, uint StreamId)>();
        var ends = new List<(PatchMemberAddress Member, uint SourceId, uint StreamId)>();
        var audio = new List<(PatchMemberAddress Member, uint SourceId, uint StreamId, short[] Samples)>();
        uint nextStream = 500;
        var router = new PatchRoutingTable(
            (member, sourceId) =>
            {
                uint stream = nextStream++;
                starts.Add((member, sourceId, stream));
                return stream;
            },
            (member, streamId, sourceId) => ends.Add((member, sourceId, streamId)),
            (member, streamId, samples, sourceId) => audio.Add((member, sourceId, streamId, samples.ToArray())),
            member => member.DestinationId + 1000);
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, target]
        });

        router.HandleCallStart(source, 77, 42);
        router.HandleAudio(source, 77, 42, new short[] { 1, 2, 3 });

        Assert.Single(starts);
        Assert.Equal(target, starts[0].Member);
        Assert.Equal((uint)1200, starts[0].SourceId);
        Assert.True(router.IsForwardTargetActive(target));
        Assert.True(router.IsPatchedTransmitStream(target, starts[0].StreamId));
        Assert.Single(audio);
        Assert.Equal([1, 2, 3], audio[0].Samples);

        router.HandleAudio(target, starts[0].StreamId, 1042, new short[] { 9 });
        Assert.Single(audio);

        router.HandleCallEnd(source, 77);
        Assert.Single(ends);
        Assert.False(router.IsForwardTargetActive(target));
        Assert.True(router.IsPatchedTransmitStream(target, starts[0].StreamId));
    }

    [Fact]
    public void OneWayGroupAcceptsOnlyItsFirstMemberAsSource()
    {
        PatchMemberAddress first = new("Alpha", 100);
        PatchMemberAddress second = new("Beta", 200);
        int starts = 0;
        var router = new PatchRoutingTable(
            (_, _) =>
            {
                starts++;
                return (uint)(700 + starts);
            },
            (_, _, _) => { },
            (_, _, _, _) => { },
            _ => 999);
        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["Dispatch"] = [first, second]
            },
            new Dictionary<string, bool> { ["Dispatch"] = true });

        router.HandleCallStart(second, 1, 42);
        Assert.Equal(0, starts);

        router.HandleCallStart(first, 2, 42);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void ChangingOneWaySourceRebuildsTheRoute()
    {
        PatchMemberAddress first = new("Alpha", 100);
        PatchMemberAddress second = new("Beta", 200);
        var starts = new List<PatchMemberAddress>();
        var ends = new List<PatchMemberAddress>();
        var router = new PatchRoutingTable(
            (member, _) =>
            {
                starts.Add(member);
                return (uint)(700 + starts.Count);
            },
            (member, _, _) => ends.Add(member),
            (_, _, _, _) => { },
            _ => 999);
        var oneWay = new Dictionary<string, bool> { ["Dispatch"] = true };

        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["Dispatch"] = [first, second]
            },
            oneWay);
        router.HandleCallStart(first, 1, 42);

        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["Dispatch"] = [second, first]
            },
            oneWay);
        router.HandleCallStart(first, 2, 42);
        router.HandleCallStart(second, 3, 42);

        Assert.Equal([second, first], starts);
        Assert.Equal([second], ends);
    }

    [Fact]
    public void FailedTargetCanRestartOnTheNextAudioBlock()
    {
        PatchMemberAddress source = new("Alpha", 100);
        PatchMemberAddress target = new("Beta", 200);
        var streams = new Queue<uint>([701, 702]);
        var starts = new List<uint>();
        var audio = new List<uint>();
        var router = new PatchRoutingTable(
            (_, _) =>
            {
                uint stream = streams.Dequeue();
                starts.Add(stream);
                return stream;
            },
            (_, _, _) => { },
            (_, stream, _, _) => audio.Add(stream),
            _ => 999);
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, target]
        });

        router.HandleAudio(source, 1, 42, new short[] { 1 });
        Assert.True(router.ReportTargetFailure(target, 701));
        router.HandleAudio(source, 1, 42, new short[] { 2 });

        Assert.Equal([701u, 702u], starts);
        Assert.Equal([701u, 702u], audio);
        Assert.True(router.IsForwardTargetActive(target));
    }

    [Fact]
    public void MembershipChangesStopOldTargetsBeforeReplacingGroup()
    {
        PatchMemberAddress source = new("Alpha", 100);
        PatchMemberAddress oldTarget = new("Beta", 200);
        PatchMemberAddress newTarget = new("Gamma", 300);
        var ends = new List<PatchMemberAddress>();
        var router = new PatchRoutingTable(
            (_, _) => 500,
            (member, _, _) => ends.Add(member),
            (_, _, _, _) => { },
            _ => 999);
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, oldTarget]
        });
        router.HandleCallStart(source, 1, 42);

        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, newTarget]
        });

        Assert.Equal([oldTarget], ends);
        Assert.False(router.IsForwardTargetActive(oldTarget));
        Assert.Equal(["Dispatch"], router.GroupNames);
    }

    [Fact]
    public void PassthroughWaitsForAUsableSourceIdAndUsesItForForwarding()
    {
        PatchMemberAddress source = new("Alpha", 100);
        PatchMemberAddress target = new("Beta", 200);
        var sourceIds = new List<uint>();
        var router = new PatchRoutingTable(
            (_, sourceId) =>
            {
                sourceIds.Add(sourceId);
                return 900;
            },
            (_, _, _) => { },
            (_, _, _, sourceId) => sourceIds.Add(sourceId),
            _ => 777);
        router.SourceIdPassthrough = true;
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, target]
        });

        router.HandleCallStart(source, 1, 0);
        Assert.Empty(sourceIds);

        router.HandleAudio(source, 1, 55, new short[] { 1 });
        Assert.Equal((uint)55, sourceIds[0]);
        Assert.Equal((uint)55, sourceIds[1]);
    }

    [Fact]
    public void SuppressesLateOutboundPacketsForExactlyTheConfiguredTimeWindow()
    {
        PatchMemberAddress source = new("Alpha", 100);
        PatchMemberAddress target = new("Beta", 200);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var sink = new RecordingPatchSink();
        var router = new PatchRoutingTable(sink, clock);
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [source, target]
        });

        router.HandleCallStart(source, 77, 42);
        router.HandleCallEnd(source, 77);

        Assert.True(router.IsPatchedTransmitStream(target, sink.StreamId));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(router.IsPatchedTransmitStream(target, sink.StreamId));
    }

    [Fact]
    public void AllToAllAllowsDifferentSubscriberToStartReverseLegImmediately()
    {
        PatchMemberAddress first = new("Alpha", 100);
        PatchMemberAddress second = new("Beta", 200);
        var starts = new List<PatchMemberAddress>();
        uint nextStreamId = 500;
        var router = new PatchRoutingTable(
            (member, _) =>
            {
                starts.Add(member);
                return nextStreamId++;
            },
            (_, _, _) => { },
            (_, _, _, _) => { },
            member => member.DestinationId + 1_000);
        router.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Dispatch"] = [first, second]
        });

        router.HandleCallStart(first, streamId: 10, sourceId: 42);
        router.HandleCallEnd(first, streamId: 10);
        router.HandleCallStart(second, streamId: 20, sourceId: 84);

        Assert.Equal([second, first], starts);
    }

    [Fact]
    public void DisabledPatchCannotFeedARewrittenEchoIntoAnotherPatch()
    {
        PatchMemberAddress firstSource = new("Alpha", 100);
        PatchMemberAddress sharedMember = new("Bravo", 200);
        PatchMemberAddress secondTarget = new("Charlie", 300);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var starts = new List<PatchMemberAddress>();
        var ends = new List<PatchMemberAddress>();
        uint nextStreamId = 500;
        var router = new PatchRoutingTable(
            (member, _) =>
            {
                starts.Add(member);
                return nextStreamId++;
            },
            (member, _, _) => ends.Add(member),
            (_, _, _, _) => { },
            _ => 999,
            clock);
        var oneWayModes = new Dictionary<string, bool>
        {
            ["First patch"] = true,
            ["Second patch"] = true
        };
        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["First patch"] = [firstSource, sharedMember],
                ["Second patch"] = [sharedMember, secondTarget]
            },
            oneWayModes);
        router.HandleCallStart(firstSource, 10, 42);

        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["Second patch"] = [sharedMember, secondTarget]
            },
            oneWayModes);
        router.HandleCallStart(sharedMember, 900, 999);
        router.HandleAudio(sharedMember, 900, 999, new short[] { 1, 2, 3 });

        Assert.Equal([sharedMember], starts);
        Assert.Equal([sharedMember], ends);

        clock.Advance(TimeSpan.FromSeconds(2));
        router.HandleCallStart(sharedMember, 901, 42);

        Assert.Equal([sharedMember, secondTarget], starts);
    }

    [Fact]
    public void EndingOneSharedTargetRouteKeepsTheOtherRouteIsolated()
    {
        PatchMemberAddress firstSource = new("Alpha", 100);
        PatchMemberAddress secondSource = new("Bravo", 200);
        PatchMemberAddress sharedTarget = new("Charlie", 300);
        PatchMemberAddress cascadeTarget = new("Delta", 400);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var starts = new List<PatchMemberAddress>();
        uint nextStreamId = 500;
        var router = new PatchRoutingTable(
            (member, _) =>
            {
                starts.Add(member);
                return nextStreamId++;
            },
            (_, _, _) => { },
            (_, _, _, _) => { },
            _ => 999,
            clock);
        var oneWayModes = new Dictionary<string, bool>
        {
            ["First patch"] = true,
            ["Second patch"] = true,
            ["Cascade patch"] = true
        };
        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["First patch"] = [firstSource, sharedTarget],
                ["Second patch"] = [secondSource, sharedTarget],
                ["Cascade patch"] = [sharedTarget, cascadeTarget]
            },
            oneWayModes);
        router.HandleCallStart(firstSource, 10, 42);
        router.HandleCallStart(secondSource, 20, 42);

        router.ApplyMemberships(
            new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
            {
                ["Second patch"] = [secondSource, sharedTarget],
                ["Cascade patch"] = [sharedTarget, cascadeTarget]
            },
            oneWayModes);
        clock.Advance(TimeSpan.FromSeconds(2));
        router.HandleCallStart(sharedTarget, 900, 42);

        Assert.Equal([sharedTarget, sharedTarget], starts);
        Assert.True(router.IsForwardTargetActive(sharedTarget));
    }

    private sealed class RecordingPatchSink : IPatchForwardingSink
    {
        public uint StreamId { get; } = 500;

        public uint BeginCall(PatchMemberAddress member, uint sourceId) => StreamId;
        public void EndCall(PatchMemberAddress member, uint streamId, uint sourceId) { }
        public void SendAudio(PatchMemberAddress member, uint streamId, ReadOnlyMemory<short> samples, uint sourceId) { }
        public uint GetFallbackSourceId(PatchMemberAddress member) => 999;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan elapsed) => utcNow += elapsed;
    }
}
