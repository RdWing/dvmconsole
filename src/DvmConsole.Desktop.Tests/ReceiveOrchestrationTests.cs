// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveOrchestrationTests
{
    [Fact]
    public async Task PhysicalEpisodeChecksPreserveSystemAndStreamIdentityWithoutPerChannelAllocation()
    {
        await using var rig = new Rig(Enumerable.Range(0, 500)
            .Select(index => Channel($"Channel {index}")).ToArray());
        ReceiveCallEpisodeSnapshot episode = rig.Traffic.ObserveIngress(
            rig.System, Frame(), DateTimeOffset.UnixEpoch, 100).EpisodeSnapshot!;
        SystemViewModel[] systems = [rig.System];
        Assert.False(MainWindowViewModel.IsEpisodePhysicallyActive(systems, episode));
        Assert.True(rig.Channels[^1].TryApplyTraffic(rig.System.Name, Frame()));
        Assert.True(MainWindowViewModel.IsEpisodePhysicallyActive(systems,
            episode with { SystemName = "TRAINING", StreamIds = new uint[] { 99, 77 } }));
        Assert.False(MainWindowViewModel.IsEpisodePhysicallyActive(systems,
            episode with { SystemName = "Another FNE" }));
        Assert.False(MainWindowViewModel.IsEpisodePhysicallyActive(systems,
            episode with { StreamIds = new uint[] { 99 } }));

        for (int index = 0; index < 100; index++)
            _ = MainWindowViewModel.IsEpisodePhysicallyActive(systems, episode);
        bool allActive = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
            allActive &= MainWindowViewModel.IsEpisodePhysicallyActive(systems, episode);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allActive);
        Assert.True(allocated <= 1_024, $"Physical episode checks allocated {allocated} bytes.");
    }

    [Fact]
    public async Task PacketDispatchDoesNotCopyEveryActiveChannel()
    {
        static async Task<long> Measure(int count)
        {
            await using var rig = new Rig(Enumerable.Range(0, count)
                .Select(index => Channel($"Channel {index}")).ToArray());
            rig.AudioChannels.AddRange(rig.Channels.Select(channel => channel.Id));
            for (ushort index = 0; index < 100; index++)
                rig.Ingress(Frame(sequence: index));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (ushort index = 100; index < 200; index++)
                rig.Ingress(Frame(sequence: index));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long one = await Measure(1);
        long fiveHundred = await Measure(500);
        Assert.True(fiveHundred <= one + 100_000,
            $"Dispatch allocation grew from {one} to {fiveHundred} bytes for unchanged active membership.");
    }

    [Fact]
    public async Task AudioAndPatchPacketsArriveInOrderBeforeDelayedPresentation()
    {
        await using var rig = new Rig(Channel("Dispatch"));
        rig.AudioChannels.Add(rig.Channels[0].Id);
        rig.PatchChannels.Add(rig.Channels[0].Id);
        for (ushort sequence = 1; sequence <= 3; sequence++)
            rig.Ingress(Frame(sequence: sequence), boundary: sequence * 100);
        Assert.Equal(new ushort[] { 1, 2, 3 }, rig.Audio.Select(item => item.Frame.PacketSequence));
        Assert.Equal(new ushort[] { 1, 2, 3 }, rig.Patch.Select(item => item.Frame.PacketSequence));
        Assert.Equal(new long[] { 100, 200, 300 }, rig.Audio.Select(item => item.Timestamp));
        Assert.Equal(Enumerable.Repeat(new[] { "record", "audio", "meter", "patch", "present" }, 3).SelectMany(item => item), rig.Events);
        Assert.Equal(3, rig.Presented.Count);
        Assert.All(rig.Presented, item => Assert.Same(rig.Channels[0], Assert.Single(item.PreEnqueuedAudioChannels)));
        Assert.False(rig.Presented[0].Decision.CanCoalescePresentation);
        Assert.True(rig.Presented[1].Decision.CanCoalescePresentation);
        Assert.Equal(0, rig.PlaybackMarks);
    }

    [Theory]
    [InlineData("dmr", FneTrafficProtocol.Dmr)]
    [InlineData("p25", FneTrafficProtocol.P25)]
    [InlineData("nxdn", FneTrafficProtocol.Nxdn)]
    public async Task TarOnlyChannelQueuesMediaWithoutLivePlayback(string mode, FneTrafficProtocol protocol)
    {
        ChannelViewModel channel = Channel("Recording", mode: mode);
        channel.SetRecordingEnabled(true);
        await using var rig = new Rig(channel);
        rig.Ingress(Frame(protocol: protocol));
        Assert.Equal(channel.Id, Assert.Single(rig.Audio).Channel);
        Assert.False(channel.IsAudioEnabled);
        Assert.Empty(rig.Patch);
        Assert.Same(channel, Assert.Single(rig.Presented[0].PreEnqueuedAudioChannels));
    }

    [Fact]
    public async Task DmrSlotAndDuplicateOwnerRemainStableAcrossStreams()
    {
        await using var rig = new Rig(Channel("Primary"), Channel("Copy"), Channel("Slot 2", slot: 2));
        rig.AudioChannels.AddRange(rig.Channels.Select(channel => channel.Id));
        rig.Ingress(Frame(slot: 0, stream: 77));
        rig.Ingress(Frame(slot: 1, stream: 88));
        Assert.Equal(new[] { rig.Channels[0].Id, rig.Channels[2].Id }, rig.Audio.Select(item => item.Channel));
        Assert.DoesNotContain(rig.Audio, item => item.Channel == rig.Channels[1].Id);
    }

    [Fact]
    public async Task FailedPriorityEnqueueIsNotReportedAsDeliveredOrMetered()
    {
        await using var rig = new Rig(Channel("Dispatch"));
        rig.AudioChannels.Add(rig.Channels[0].Id);
        rig.PatchChannels.Add(rig.Channels[0].Id);
        rig.AcceptAudio = false;
        rig.AcceptPatch = false;
        rig.Ingress(Frame());
        Assert.Empty(rig.Presented[0].PreEnqueuedAudioChannels);
        Assert.Empty(rig.Presented[0].PreEnqueuedPatchChannels);
        Assert.Equal(0, rig.MeterMarks);
        Assert.Equal(1, rig.Diagnostics);
    }

    [Fact]
    public async Task CapturedEpisodeDoesNotChangeWhenNewPhysicalStreamArrives()
    {
        await using var rig = new Rig(Channel("Dispatch"));
        ReceivePacketDecisionEnvelope first = rig.Traffic.ObserveIngress(rig.System, Frame(stream: 77), DateTimeOffset.UnixEpoch, 100);
        ReceivePacketDecisionEnvelope next = rig.Traffic.ObserveIngress(rig.System, Frame(stream: 78), DateTimeOffset.UnixEpoch.AddMilliseconds(100), 200);
        Assert.Equal(first.EpisodeSnapshot!.EpisodeId, next.EpisodeSnapshot!.EpisodeId);
        Assert.Single(first.EpisodeSnapshot.StreamIds);
        Assert.Equal(2, next.EpisodeSnapshot.StreamIds.Count);
        Assert.Equal(100, first.ReceivedTimestamp);
        Assert.False(next.CanCoalescePresentation);
    }

    [Fact]
    public async Task CandidateFilterAndCapturedRoutingSurviveDelayedProjection()
    {
        await using var rig = new Rig(Channel("Owner"), Channel("Copy"));
        ChannelViewModel owner = rig.Channels[0];
        ReceivePacketDecisionEnvelope decision = rig.Traffic.ObserveIngress(
            rig.System, Frame(), DateTimeOffset.UnixEpoch, 100, [owner.Id]);
        rig.Traffic.ObserveIngress(rig.System, Frame(stream: 78), DateTimeOffset.UnixEpoch.AddMilliseconds(50), 200);
        Assert.Equal(owner, Assert.Single(rig.Traffic.ResolvePresentationCandidates(rig.System, decision)));
        Assert.Equal(77u, decision.Traffic.StreamId);
        Assert.Equal(77u, decision.EpisodeSnapshot!.PrimaryStreamId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SuppressedOrClosingSessionRejectsLateIngress(bool suppressed, bool disposing)
    {
        await using var rig = new Rig(Channel("Dispatch")) { InputsSuppressed = suppressed, IsDisposing = disposing };
        rig.Ingress(Frame());
        Assert.Empty(rig.Events);
        Assert.Empty(rig.Presented);
    }

    [Theory]
    [InlineData(123, 456, 123)]
    [InlineData(0, 456, 456)]
    [InlineData(0, 0, 0)]
    public async Task TimestampPrecedenceIsPreserved(long boundary, long fneBoundary, long expected)
    {
        await using var rig = new Rig(Channel("Dispatch"));
        rig.AudioChannels.Add(rig.Channels[0].Id);
        FneTrafficFrame frame = Frame(fneBoundary: fneBoundary);
        rig.Ingress(frame, boundary);
        Assert.Equal(expected == 0 ? frame.FneBoundaryTimestamp : expected, Assert.Single(rig.Audio).Timestamp);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 1)]
    public async Task QueueDropsAreCountedOnceAndOnlyAcceptedFramesUpdatePlayback(bool accepted, bool dropped, int diagnostics)
    {
        await using var rig = new Rig(Channel("Dispatch")) { AcceptAudio = accepted, DropAudio = dropped };
        rig.Dispatch.EnqueueAudio(rig.Channels[0].Id, Frame(), 0);
        Assert.Equal(dropped ? 1 : 0, rig.Dropped);
        Assert.Equal(diagnostics, rig.Diagnostics);
        Assert.Equal(accepted ? 1 : 0, rig.PlaybackMarks);
        Assert.Equal(900, Assert.Single(rig.Audio).Timestamp);
    }

    [Fact]
    public async Task CompletionWaitsForQueuedPacketsAndPublishesSummaryAfterFailure()
    {
        await using var rig = new Rig(Channel("Dispatch")) { CompletionFailure = new IOException("decoder flush") };
        Task completion = rig.Dispatch.FinalizeStreamAsync(rig.Channels[0].Id, 77, DateTimeOffset.UnixEpoch);
        await rig.FinalizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, rig.CompletedStreams);
        Assert.Equal(0, rig.FinalSummaries);
        rig.QueuedPacketsFinished.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rig.CompletedStreams);
        Assert.Equal(1, rig.FinalSummaries);
        Assert.Same(rig.CompletionFailure, Assert.Single(rig.CleanupFailures));
        Assert.Equal(new[] { "after-stream", "complete", "summary", "failure" }, rig.Events);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task DisposedQueueFailureIsIgnoredOnlyWhenShutdownOwnsCleanup(bool disposing, int failures)
    {
        await using var rig = new Rig(Channel("Dispatch")) { IsDisposing = disposing, CompletionFailure = new ObjectDisposedException("queue") };
        rig.QueuedPacketsFinished.SetResult();
        await rig.Dispatch.FinalizeStreamAsync(rig.Channels[0].Id, 77, DateTimeOffset.UnixEpoch);
        Assert.Equal(failures, rig.CleanupFailures.Count);
        Assert.Equal(1, rig.FinalSummaries);
    }

    [Fact]
    public async Task DestinationlessTerminatorReportsOnlyAcceptedOwners()
    {
        await using var rig = new Rig(Channel("One"), Channel("Two", destination: "200"));
        rig.AudioChannels.AddRange(rig.Channels.Select(channel => channel.Id));
        rig.Ingress(Frame(destination: 100));
        rig.Ingress(Frame(destination: 200));
        rig.RejectedAudioChannels.Add(rig.Channels[0].Id);
        rig.Ingress(Frame(sequence: 2, destination: 0, terminator: true));
        Assert.Same(rig.Channels[1], Assert.Single(rig.Presented.Last().PreEnqueuedAudioChannels));
        Assert.Equal(1, rig.Diagnostics);
    }

    [Fact]
    public async Task LateTerminatorCannotReviveAnExpiredIngressStream()
    {
        await using var rig = new Rig(Channel("Dispatch"));
        rig.Traffic.ObserveIngress(rig.System, Frame(), DateTimeOffset.UnixEpoch, 100);
        ReceivePacketDecisionEnvelope late = rig.Traffic.ObserveIngress(
            rig.System, Frame(sequence: 2, terminator: true), DateTimeOffset.UnixEpoch.AddSeconds(3), 200);
        Assert.True(late.Routing.TryGet(rig.Channels[0].SessionDefinition.RouteKey, out ReceiveIngressRouteDecision route));
        Assert.Contains(route.PrecedingDecisions, item => item.StreamDecision.Transition == ReceiveStreamTransition.GraceExpired);
        Assert.Equal(ReceiveStreamTransition.IgnoredLate, route.StreamDecision.Transition);
        Assert.Empty(route.ActiveStreamIds);
    }

    [Fact]
    public async Task PhysicalCompletionLeavesTheLogicalEpisodeAvailableForRecordingContinuation()
    {
        ChannelViewModel channel = Channel("TAR");
        channel.SetRecordingEnabled(true);
        await using var rig = new Rig(channel);
        var first = rig.Traffic.ObserveIngress(rig.System, Frame(), DateTimeOffset.UnixEpoch, 100);
        rig.Traffic.EndPhysicalStream(rig.System.Name, FneTrafficProtocol.Dmr, channel.Id, 77,
            DateTimeOffset.UnixEpoch.AddMilliseconds(20), ReceivePhysicalEndReason.Replaced);
        await rig.FinalizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, rig.CompletedStreams);
        var next = rig.Traffic.ObserveIngress(rig.System, Frame(stream: 78), DateTimeOffset.UnixEpoch.AddMilliseconds(100), 200);
        Assert.Equal(first.EpisodeSnapshot!.EpisodeId, next.EpisodeSnapshot!.EpisodeId);
        Assert.Equal(77u, next.EpisodeSnapshot.PrimaryStreamId);
        Assert.True(channel.IsRecordingEnabled);
        rig.QueuedPacketsFinished.SetResult();
        await rig.FinalizationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rig.CompletedStreams);
    }

    [Fact]
    public async Task RouteExpiryProjectsTheSelectedOwnerThroughThePresentationPort()
    {
        await using var rig = new Rig(Channel("Owner"), Channel("Copy"));
        rig.AudioChannels.Add(rig.Channels[0].Id);
        rig.Traffic.ObserveIngress(rig.System, Frame(), DateTimeOffset.UnixEpoch, 100);
        rig.Traffic.Advance(DateTimeOffset.UnixEpoch.AddSeconds(3));
        Assert.Contains(rig.Projections, item => item.Channel == rig.Channels[0].Id &&
            item.Decision.StreamDecision.EndedStreamId == 77);
        Assert.DoesNotContain(rig.Projections, item => item.Channel == rig.Channels[1].Id);
    }

    private static ChannelViewModel Channel(string name, byte slot = 1, string mode = "dmr", string destination = "100")
        => new(new ChannelConfiguration { Name = name, System = "Training", Tgid = destination, Slot = slot, Mode = mode });
    private static FneTrafficFrame Frame(ushort sequence = 1, uint stream = 77, byte slot = 0,
        FneTrafficProtocol protocol = FneTrafficProtocol.Dmr, long fneBoundary = 0,
        uint destination = 100, bool terminator = false)
        => new(protocol, 1, 42, destination, slot, "GROUP", terminator ? "TERMINATOR" : "VOICE",
            terminator ? "TERMINATOR_WITH_LC" : protocol == FneTrafficProtocol.P25 ? "LDU1" : "VOICE",
            sequence, stream, [], fneBoundary);

    private sealed class Rig : IReceiveMediaState, IReceiveMediaWork, IReceiveMediaPresentation, IReceiveTrafficPresentation, IAsyncDisposable
    {
        private readonly ChannelId[] candidateChannels;

        public Rig(params ChannelViewModel[] channels)
        {
            Channels = channels;
            candidateChannels = channels.Select(channel => channel.Id).ToArray();
            System = new(new FneConnectionOptions("Training", "Console", "127.0.0.1", 62031, 1, null, false, null),
                "Training", "127.0.0.1:62031", channels);
            var clock = new FixedClock();
            Dispatch = new(this, this, clock);
            Traffic = new([System], channels.ToDictionary(channel => channel.Id), Episodes, this, Dispatch, this, clock);
        }
        public ChannelViewModel[] Channels { get; }
        public SystemViewModel System { get; }
        public ReceiveCallEpisodeTracker Episodes { get; } = new();
        public ReceiveTrafficCoordinator Traffic { get; }
        public ReceiveMediaDispatchCoordinator Dispatch { get; }
        public bool IsDisposing { get; set; }
        public bool InputsSuppressed { get; set; }
        public List<ChannelId> AudioChannels { get; } = [];
        public List<ChannelId> PatchChannels { get; } = [];
        public IReadOnlyList<ChannelId> ActiveAudioChannels => AudioChannels;
        public IReadOnlyList<ChannelId> ActivePatchChannels => PatchChannels;
        public List<(ChannelId Channel, FneTrafficFrame Frame, long Timestamp)> Audio { get; } = [];
        public List<(ChannelId Channel, FneTrafficFrame Frame, long? Timestamp)> Patch { get; } = [];
        public List<SystemTrafficWorkItem> Presented { get; } = [];
        public List<(ChannelId Channel, ReceiveRouteProjectionDecision Decision)> Projections { get; } = [];
        public List<string> Events { get; } = [];
        public List<Exception> CleanupFailures { get; } = [];
        public bool AcceptAudio { get; set; } = true;
        public bool AcceptPatch { get; set; } = true;
        public bool DropAudio { get; set; }
        public HashSet<ChannelId> RejectedAudioChannels { get; } = [];
        public int Dropped { get; private set; }
        public int Diagnostics { get; private set; }
        public int MeterMarks { get; private set; }
        public int PlaybackMarks { get; private set; }
        public int CompletedStreams { get; private set; }
        public int FinalSummaries { get; private set; }
        public Exception? CompletionFailure { get; init; }
        public TaskCompletionSource FinalizationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinalizationFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource QueuedPacketsFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Ingress(FneTrafficFrame frame, long boundary = 0)
            => Traffic.HandleIngress(System, new RadioTrafficRecord(SystemId.FromName(System.Name), candidateChannels, frame,
                DateTimeOffset.UnixEpoch.AddMilliseconds(frame.PacketSequence), boundary));
        public bool IsAudioActive(ChannelId channel) => AudioChannels.Contains(channel);
        public bool IsPatchActive(ChannelId channel) => PatchChannels.Contains(channel);
        public bool IsAudioTrackingStream(ChannelId channel, uint streamId) => Audio.Any(item => item.Channel == channel && item.Frame.StreamId == streamId);
        public bool IsPatchTrackingStream(ChannelId channel, uint streamId) => Patch.Any(item => item.Channel == channel && item.Frame.StreamId == streamId);
        public bool IsTrackingStream(ChannelId channel, uint streamId) => false;
        public bool EnqueueAudio(ChannelId channel, FneTrafficFrame traffic, long ingressTimestamp, out bool droppedFrame)
        {
            Events.Add("audio");
            Audio.Add((channel, traffic, ingressTimestamp));
            droppedFrame = DropAudio;
            return AcceptAudio && !RejectedAudioChannels.Contains(channel);
        }
        public bool EnqueuePatch(ChannelId channel, FneTrafficFrame traffic, long? ingressTimestamp)
        {
            Events.Add("patch");
            Patch.Add((channel, traffic, ingressTimestamp));
            return AcceptPatch;
        }
        public void StopPatchSource(ChannelId channel, uint streamId) => Events.Add("stop-patch");
        public async Task RunAfterStreamAsync(ChannelId channel, uint streamId, Func<CancellationToken, Task> continuation)
        {
            Events.Add("after-stream");
            FinalizationEntered.TrySetResult();
            await QueuedPacketsFinished.Task;
            try { await continuation(CancellationToken.None); }
            finally { FinalizationFinished.TrySetResult(); }
        }
        public Task CompleteStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt, CancellationToken cancellationToken)
        {
            Events.Add("complete");
            CompletedStreams++;
            if (CompletionFailure is not null) throw CompletionFailure;
            return Task.CompletedTask;
        }
        public void RecordDroppedFrame(ChannelId channel) => Dropped++;
        public void PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now) => Diagnostics++;
        public void MarkAudioMeter(ChannelId channel, uint streamId, bool ended) { MeterMarks++; Events.Add("meter"); }
        public void MarkPlaybackActive(ChannelId channel, uint sourceId, uint streamId) => PlaybackMarks++;
        public void PublishFinalJitterSummary(ChannelId channel, uint streamId) { FinalSummaries++; Events.Add("summary"); }
        public void ReportCleanupFailure(ChannelId channel, uint streamId, Exception failure) { CleanupFailures.Add(failure); Events.Add("failure"); }
        public void RecordIngress(SystemViewModel system, FneTrafficFrame traffic) => Events.Add("record");
        public void Present(SystemViewModel system, SystemTrafficWorkItem workItem) { Presented.Add(workItem); Events.Add("present"); }
        public void ProjectLifecycle(ChannelId channel, ReceiveRouteProjectionDecision decision, DateTimeOffset now) => Projections.Add((channel, decision));
        public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) => Events.Add("log");
        public ValueTask DisposeAsync() => System.DisposeAsync();
    }
    private sealed class FixedClock : TimeProvider
    {
        public override long GetTimestamp() => 900;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
