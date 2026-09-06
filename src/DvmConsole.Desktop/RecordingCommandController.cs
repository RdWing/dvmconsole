// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal interface IRecordingCommandSession
{
    bool TryGetRecordingPath(CallRecordingMetadata metadata, out string recordingPath);
    Task StartPlaybackAsync(RecordingId? recordingId, string recordingPath);
    Task StopPlaybackAsync();
    Task StopPlaybackIfActiveAsync(RecordingId? recordingId, string recordingPath);
    bool DeleteRecording(CallRecordingMetadata metadata);
    void RevealRecording(string recordingPath);
    void PublishRecordingCommand(RecordingCommandNotification notification);
    ValueTask PublishRecordingCommandAsync(RecordingCommandNotification notification);
}

internal enum RecordingCommandNotificationKind
{
    Status,
    MissingRecording,
    DeleteFailed,
    Deleted
}

internal sealed record RecordingCommandNotification(
    RecordingCommandNotificationKind Kind,
    string Message,
    CallRecordingMetadata? Recording = null);

/// <summary>
/// Owns operator recording commands independently from the History projection
/// and the binding-compatible main-window facade.
/// </summary>
internal sealed class RecordingCommandController
{
    private readonly IRecordingCommandSession session;

    public RecordingCommandController(IRecordingCommandSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void Open(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!TryResolvePlayableRecording(metadata, out string recordingPath))
        {
            PublishMissingRecording(metadata);
            return;
        }

        try
        {
            session.RevealRecording(recordingPath);
            PublishStatus($"Opened recording location: {metadata.FileName}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            PublishStatus($"Unable to show recording in its folder: {exception.Message}");
        }
    }

    public async Task PlayAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!TryResolvePlayableRecording(metadata, out string recordingPath))
        {
            await PublishMissingRecordingAsync(metadata).ConfigureAwait(false);
            return;
        }

        try
        {
            await session
                .StartPlaybackAsync(RecordingIdentity.Parse(metadata), recordingPath)
                .ConfigureAwait(false);
            await PublishStatusAsync(
                $"Playing: {RecordingPlaybackText.Describe(metadata.ChannelName, metadata.UtcStartTime)}")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            await PublishStatusAsync($"Unable to play recording: {exception.Message}").ConfigureAwait(false);
        }
    }

    public async Task PlayHistoryAsync(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Recording is not CallRecordingMetadata metadata)
        {
            await PublishStatusAsync("No TAR recording is available for this event.").ConfigureAwait(false);
            return;
        }

        await PlayAsync(metadata).ConfigureAwait(false);
    }

    public Task ToggleHistoryPlaybackAsync(CallHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsRecordingPlaying
            ? StopAsync()
            : PlayHistoryAsync(entry);
    }

    public async Task StopAsync()
    {
        await session.StopPlaybackAsync().ConfigureAwait(false);
        await PublishStatusAsync("Recording playback stopped.").ConfigureAwait(false);
    }

    public async Task DeleteAsync(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        try
        {
            if (session.TryGetRecordingPath(metadata, out string recordingPath))
            {
                await session
                    .StopPlaybackIfActiveAsync(RecordingIdentity.Parse(metadata), recordingPath)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await PublishStatusAsync(
                $"Unable to stop recording playback for deletion: {exception.Message}").ConfigureAwait(false);
            return;
        }

        if (!session.DeleteRecording(metadata))
        {
            await session.PublishRecordingCommandAsync(new RecordingCommandNotification(
                    RecordingCommandNotificationKind.DeleteFailed,
                    "The selected recording could not be deleted.",
                    metadata))
                .ConfigureAwait(false);
            return;
        }

        await session.PublishRecordingCommandAsync(new RecordingCommandNotification(
                RecordingCommandNotificationKind.Deleted,
                $"Deleted recording: {metadata.FileName}",
                metadata))
            .ConfigureAwait(false);
    }

    private bool TryResolvePlayableRecording(
        CallRecordingMetadata metadata,
        out string recordingPath)
    {
        if (metadata.IsPlayable && session.TryGetRecordingPath(metadata, out recordingPath))
            return true;

        recordingPath = string.Empty;
        return false;
    }

    private void PublishMissingRecording(CallRecordingMetadata metadata)
        => session.PublishRecordingCommand(new RecordingCommandNotification(
            RecordingCommandNotificationKind.MissingRecording,
            "The selected recording file is no longer available.",
            metadata));

    private ValueTask PublishMissingRecordingAsync(CallRecordingMetadata metadata)
        => session.PublishRecordingCommandAsync(new RecordingCommandNotification(
            RecordingCommandNotificationKind.MissingRecording,
            "The selected recording file is no longer available.",
            metadata));

    private void PublishStatus(string message)
        => session.PublishRecordingCommand(new RecordingCommandNotification(
            RecordingCommandNotificationKind.Status,
            message));

    private ValueTask PublishStatusAsync(string message)
        => session.PublishRecordingCommandAsync(new RecordingCommandNotification(
            RecordingCommandNotificationKind.Status,
            message));
}

internal static class RecordingIdentity
{
    public static RecordingId? Parse(CallRecordingMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Guid.TryParse(metadata.RecordingId, out Guid value)
            ? new RecordingId(value)
            : null;
    }
}

internal static class RecordingFileLauncher
{
    public static void Reveal(string recordingPath)
        => Process.Start(CreateStartInfo(
            recordingPath,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS()));

    public static ProcessStartInfo CreateStartInfo(
        string recordingPath,
        bool isWindows,
        bool isMacOS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        string fullPath = Path.GetFullPath(recordingPath);

        if (isWindows)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(fullPath);
            return startInfo;
        }

        if (isMacOS)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(fullPath);
            return startInfo;
        }

        return new ProcessStartInfo
        {
            FileName = Path.GetDirectoryName(fullPath) ?? fullPath,
            UseShellExecute = true
        };
    }
}
