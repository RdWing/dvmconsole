// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingCommandControllerTests
{
    [Fact]
    public async Task PlayUsesStableIdentityAndPublishesSuccess()
    {
        Guid identity = Guid.NewGuid();
        CallRecordingMetadata metadata = PlayableRecording(identity);
        var session = new TestSession(metadata.FilePath);
        var controller = new RecordingCommandController(session);

        await controller.PlayAsync(metadata);

        Assert.Equal(new RecordingId(identity), session.StartedRecordingId);
        Assert.Equal(metadata.FilePath, session.StartedPath);
        string status = Assert.Single(session.Notifications).Message;
        Assert.Contains(metadata.ChannelName, status);
        Assert.Contains(metadata.UtcStartTime.ToLocalTime().ToString("HH:mm:ss"), status);
        Assert.DoesNotContain(metadata.FileName, status);
    }

    [Fact]
    public async Task DeleteStopsPlaybackBeforeRemovingAndPublishingCatalogChange()
    {
        CallRecordingMetadata metadata = PlayableRecording(Guid.NewGuid());
        var session = new TestSession(metadata.FilePath);
        var controller = new RecordingCommandController(session);

        await controller.DeleteAsync(metadata);

        Assert.Equal(["stop", "delete"], session.OperationOrder);
        RecordingCommandNotification notification = Assert.Single(session.Notifications);
        Assert.Equal(RecordingCommandNotificationKind.Deleted, notification.Kind);
        Assert.Same(metadata, notification.Recording);
    }

    [Fact]
    public async Task FailedStopPreventsDeleteAndReportsFailure()
    {
        CallRecordingMetadata metadata = PlayableRecording(Guid.NewGuid());
        var session = new TestSession(metadata.FilePath)
        {
            StopFailure = new IOException("output is busy")
        };
        var controller = new RecordingCommandController(session);

        await controller.DeleteAsync(metadata);

        Assert.Equal(["stop"], session.OperationOrder);
        Assert.Contains("output is busy", Assert.Single(session.Notifications).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRecordingRequestsCatalogRefreshInsteadOfLaunching()
    {
        CallRecordingMetadata metadata = PlayableRecording(Guid.NewGuid());
        var session = new TestSession(recordingPath: null);
        var controller = new RecordingCommandController(session);

        controller.Open(metadata);

        RecordingCommandNotification notification = Assert.Single(session.Notifications);
        Assert.Equal(RecordingCommandNotificationKind.MissingRecording, notification.Kind);
        Assert.False(session.RevealCalled);
    }

    private static CallRecordingMetadata PlayableRecording(Guid identity)
        => new()
        {
            RecordingId = identity.ToString("N"),
            FilePath = "/recordings/call.opus",
            FileName = "call.opus",
            PlaybackValidated = true,
            DurationMs = 1_000,
            FileSizeBytes = 1_000,
            ActiveSampleCount = 8_000,
            PeakAmplitude = 100
        };

    private sealed class TestSession(string? recordingPath) : IRecordingCommandSession
    {
        public List<string> OperationOrder { get; } = [];
        public List<RecordingCommandNotification> Notifications { get; } = [];
        public RecordingId? StartedRecordingId { get; private set; }
        public string? StartedPath { get; private set; }
        public Exception? StopFailure { get; set; }
        public bool RevealCalled { get; private set; }

        public bool TryGetRecordingPath(CallRecordingMetadata metadata, out string resolvedPath)
        {
            resolvedPath = recordingPath ?? string.Empty;
            return recordingPath is not null;
        }

        public Task StartPlaybackAsync(RecordingId? recordingId, string path)
        {
            StartedRecordingId = recordingId;
            StartedPath = path;
            return Task.CompletedTask;
        }

        public Task StopPlaybackAsync() => Task.CompletedTask;

        public Task StopPlaybackIfActiveAsync(RecordingId? recordingId, string path)
        {
            OperationOrder.Add("stop");
            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }

        public bool DeleteRecording(CallRecordingMetadata metadata)
        {
            OperationOrder.Add("delete");
            return true;
        }

        public void RevealRecording(string path)
            => RevealCalled = true;

        public void PublishRecordingCommand(RecordingCommandNotification notification)
            => Notifications.Add(notification);

        public ValueTask PublishRecordingCommandAsync(RecordingCommandNotification notification)
        {
            Notifications.Add(notification);
            return ValueTask.CompletedTask;
        }
    }
}
