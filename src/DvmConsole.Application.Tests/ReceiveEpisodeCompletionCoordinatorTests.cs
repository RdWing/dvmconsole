using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceiveEpisodeCompletionCoordinatorTests
{
    [Fact]
    public async Task CompletesDistinctChannelsBeforeStoppingDistinctRecordingTargets()
    {
        ChannelId first = CreateChannelId("first", 100);
        ChannelId second = CreateChannelId("second", 101);
        ChannelId recordingTarget = CreateChannelId("recording", 102);
        var port = new RecordingPort(recordingTarget);
        var coordinator = new ReceiveEpisodeCompletionCoordinator(port);

        await coordinator.CompleteAsync(
            new ReceiveEpisodeCompletion(42, 7, [7, 8, 7, 0]),
            [first, second, first]);

        Assert.Equal(2, port.Drains.Count);
        Assert.All(port.Drains, drain => Assert.Equal([7u, 8u], drain.StreamIds));
        Assert.Equal(
            [
                $"playback:{first}:42",
                $"playback:{second}:42",
                $"recording:{recordingTarget}:42"
            ],
            port.Events);
    }

    [Fact]
    public async Task UsesPrimaryStreamWhenNoEpisodeStreamListIsAvailable()
    {
        ChannelId channel = CreateChannelId("primary", 200);
        var port = new RecordingPort(channel);
        var coordinator = new ReceiveEpisodeCompletionCoordinator(port);

        await coordinator.CompleteAsync(
            new ReceiveEpisodeCompletion(9, 77, []),
            [channel]);

        Assert.Equal([77u], Assert.Single(port.Drains).StreamIds);
    }

    [Fact]
    public async Task RejectsAnEpisodeWithoutAnyPhysicalStreamIdentity()
    {
        ChannelId channel = CreateChannelId("invalid", 300);
        var coordinator = new ReceiveEpisodeCompletionCoordinator(new RecordingPort(channel));

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CompleteAsync(
            new ReceiveEpisodeCompletion(10, 0, [0]),
            [channel]));
    }

    private static ChannelId CreateChannelId(string instance, uint destinationId)
        => new(new ChannelSessionId(
            "System",
            ChannelProtocol.P25,
            destinationId,
            slot: 0,
            instance));

    private sealed class RecordingPort(ChannelId recordingTarget) : IReceiveEpisodeCompletionPort
    {
        public List<(ChannelId ChannelId, IReadOnlyCollection<uint> StreamIds)> Drains { get; } = [];
        public List<string> Events { get; } = [];

        public async Task RunAfterStreamsAsync(
            ChannelId channelId,
            IReadOnlyCollection<uint> streamIds,
            Func<Task> continuation)
        {
            Drains.Add((channelId, streamIds));
            await continuation();
        }

        public Task CompletePlaybackAsync(ChannelId channelId, long episodeId)
        {
            Events.Add($"playback:{channelId}:{episodeId}");
            return Task.CompletedTask;
        }

        public ChannelId? ResolveRecordingTarget(ChannelId channelId)
            => recordingTarget;

        public void StopRecording(ChannelId channelId, long episodeId)
            => Events.Add($"recording:{channelId}:{episodeId}");
    }
}
