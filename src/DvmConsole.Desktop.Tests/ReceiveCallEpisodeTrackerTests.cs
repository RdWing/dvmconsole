using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveCallEpisodeTrackerTests
{
    [Fact]
    public void UsesProtocolFramingToDefineContinuationWindows()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1_800),
            ReceiveCallEpisodePolicy.GetContinuationWindow(FneTrafficProtocol.P25));
        Assert.Equal(TimeSpan.FromMilliseconds(600),
            ReceiveCallEpisodePolicy.GetContinuationWindow(FneTrafficProtocol.Dmr));
        Assert.Equal(TimeSpan.FromMilliseconds(800),
            ReceiveCallEpisodePolicy.GetContinuationWindow(FneTrafficProtocol.Nxdn));
        Assert.Equal(TimeSpan.Zero,
            ReceiveCallEpisodePolicy.GetContinuationWindow(FneTrafficProtocol.Analog));
    }

    [Fact]
    public void CoalescesSameSourceFloodAtContinuationBoundary()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveCallEpisodeObservation first = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223),
            now)!.Value;
        ReceiveCallEpisodeObservation second = tracker.Observe(
            "SKYNET",
            P25(streamId: 11, sourceId: 3_206_223),
            now.AddMilliseconds(1_800))!.Value;

        Assert.True(first.EpisodeStarted);
        Assert.False(second.EpisodeStarted);
        Assert.Equal(first.EpisodeId, second.EpisodeId);
        Assert.Equal((uint)10, second.PrimaryStreamId);
        Assert.Equal(2, second.StreamCount);
    }

    [Fact]
    public void StartsNewEpisodeBeyondContinuationWindow()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveCallEpisodeObservation first = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223),
            now)!.Value;
        ReceiveCallEpisodeObservation second = tracker.Observe(
            "SKYNET",
            P25(streamId: 11, sourceId: 3_206_223),
            now.AddMilliseconds(1_801))!.Value;

        Assert.NotEqual(first.EpisodeId, second.EpisodeId);
        Assert.True(second.EpisodeStarted);
    }

    [Fact]
    public void NeverCoalescesDifferentSourcesOnTheSameTalkgroup()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveCallEpisodeObservation first = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223),
            now)!.Value;
        ReceiveCallEpisodeObservation second = tracker.Observe(
            "SKYNET",
            P25(streamId: 11, sourceId: 3_211_515),
            now.AddMilliseconds(100))!.Value;

        Assert.NotEqual(first.EpisodeId, second.EpisodeId);
        Assert.Equal((uint)11, second.PrimaryStreamId);
    }

    [Fact]
    public void ReusedPhysicalStreamIdCannotMergeDifferentSources()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveCallEpisodeObservation first = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223),
            now)!.Value;
        ReceiveCallEpisodeObservation second = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_211_515),
            now.AddMilliseconds(100))!.Value;

        Assert.NotEqual(first.EpisodeId, second.EpisodeId);
        Assert.Equal((uint)10, second.PrimaryStreamId);
    }

    [Fact]
    public void NeverCoalescesDifferentDmrSlotsOrCallTypes()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveCallEpisodeObservation first = tracker.Observe(
            "Local",
            Dmr(streamId: 20, slot: 1, callType: "GROUP"),
            now)!.Value;
        ReceiveCallEpisodeObservation differentSlot = tracker.Observe(
            "Local",
            Dmr(streamId: 21, slot: 2, callType: "GROUP"),
            now.AddMilliseconds(100))!.Value;
        ReceiveCallEpisodeObservation differentCallType = tracker.Observe(
            "Local",
            Dmr(streamId: 22, slot: 1, callType: "PRIVATE"),
            now.AddMilliseconds(200))!.Value;

        Assert.NotEqual(first.EpisodeId, differentSlot.EpisodeId);
        Assert.NotEqual(first.EpisodeId, differentCallType.EpisodeId);
    }

    [Fact]
    public void TerminatorResolvesWithoutExtendingVoiceActivity()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        ReceiveCallEpisodeObservation first = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223),
            now)!.Value;

        ReceiveCallEpisodeObservation terminator = tracker.Observe(
            "SKYNET",
            P25(streamId: 10, sourceId: 3_206_223, terminator: true),
            now.AddSeconds(1))!.Value;
        Assert.Equal(first.EpisodeId, terminator.EpisodeId);
        Assert.Empty(tracker.Advance(now.AddMilliseconds(1_800)));

        ReceiveCallEpisodeSnapshot completed = Assert.Single(
            tracker.Advance(now.AddMilliseconds(1_801)));
        Assert.Equal(first.EpisodeId, completed.EpisodeId);
        Assert.Equal(now, completed.LastActivityAt);
    }

    [Fact]
    public void CompletedEpisodeRetainsPhysicalMappingsForQueuedAudio()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        tracker.Observe("SKYNET", P25(streamId: 10, sourceId: 3_206_223), now);
        tracker.Observe("SKYNET", P25(streamId: 11, sourceId: 3_206_223), now.AddMilliseconds(100));

        tracker.Advance(now.AddSeconds(2));

        Assert.True(tracker.TryGet("SKYNET", FneTrafficProtocol.P25, 10, out var first));
        Assert.True(tracker.TryGet("SKYNET", FneTrafficProtocol.P25, 11, out var second));
        Assert.Equal(first.EpisodeId, second.EpisodeId);
        Assert.Equal(new uint[] { 10, 11 }, first.StreamIds);
    }

    [Fact]
    public void OaklandFireFloodBecomesTwoEpisodesBecauseSourcesAreInterleaved()
    {
        var tracker = new ReceiveCallEpisodeTracker();
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        (int Milliseconds, uint SourceId, uint StreamId)[] observed =
        [
            (0, 3_211_515, 4_083_396_432),
            (1_124, 3_206_223, 1_657_635_980),
            (1_342, 3_211_515, 226_341_030),
            (1_362, 3_206_223, 2_220_514_020),
            (1_617, 3_211_515, 917_417_015),
            (1_718, 3_206_223, 375_802_641),
            (1_801, 3_211_515, 1_111_477_935),
            (1_932, 3_206_223, 2_546_558_541),
            (1_999, 3_211_515, 849_321_797),
            (2_096, 3_206_223, 1_556_409_645),
            (2_355, 3_211_515, 2_600_778_420),
            (2_430, 3_206_223, 3_651_823_839),
            (2_606, 3_211_515, 2_414_616_978),
            (2_615, 3_206_223, 4_139_006_003),
            (2_836, 3_211_515, 3_127_177_880),
            (2_964, 3_206_223, 53_370_303),
            (3_125, 3_206_223, 843_930_792),
            (3_284, 3_211_515, 419_622_876),
            (3_477, 3_206_223, 3_240_674_134),
            (3_594, 3_211_515, 4_052_461_307),
            (3_666, 3_206_223, 2_901_990_152),
            (3_794, 3_211_515, 3_374_572_107)
        ];

        var episodesBySource = new Dictionary<uint, HashSet<long>>();
        foreach ((int milliseconds, uint sourceId, uint streamId) in observed)
        {
            ReceiveCallEpisodeObservation episode = tracker.Observe(
                "SKYNET",
                P25(streamId, sourceId),
                start.AddMilliseconds(milliseconds))!.Value;
            if (!episodesBySource.TryGetValue(sourceId, out HashSet<long>? sourceEpisodes))
            {
                sourceEpisodes = [];
                episodesBySource.Add(sourceId, sourceEpisodes);
            }
            sourceEpisodes.Add(episode.EpisodeId);
        }

        Assert.Equal(2, episodesBySource.Count);
        Assert.All(episodesBySource.Values, episodeIds => Assert.Single(episodeIds));
        Assert.NotEqual(
            episodesBySource[3_206_223].Single(),
            episodesBySource[3_211_515].Single());

        Assert.True(tracker.TryGet(
            "SKYNET", FneTrafficProtocol.P25, 2_901_990_152, out var oaklandFirst));
        Assert.True(tracker.TryGet(
            "SKYNET", FneTrafficProtocol.P25, 3_374_572_107, out var oaklandSecond));
        Assert.Equal(11, oaklandFirst.StreamIds.Count);
        Assert.Equal(11, oaklandSecond.StreamIds.Count);
    }

    private static FneTrafficFrame P25(
        uint streamId,
        uint sourceId,
        bool terminator = false)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            destinationId: 2_971,
            slot: null,
            callType: "GROUP",
            frameType: terminator ? "TERMINATOR" : "VOICE",
            subtype: terminator ? "TDU" : "LDU1",
            packetSequence: 1,
            streamId,
            payload: new byte[200]);

    private static FneTrafficFrame Dmr(uint streamId, byte slot, string callType)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot,
            callType,
            frameType: "VOICE",
            subtype: "VOICE",
            packetSequence: 1,
            streamId,
            payload: new byte[55]);
}
