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
