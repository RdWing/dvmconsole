// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Audio;
using Foundation;
using MediaPlayer;

namespace DvmConsole.iOS;

/// <summary>One system player for the active console's listening intent.</summary>
internal sealed class IosListeningControls(IosAudioSessionOwner audio) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly List<(MPRemoteCommand Command, NSObject Target)> handlers = [];
    private ConsoleReceiveSession? active;
    private ConsoleReceiveSession? resuming;
    private MPRemoteCommandCenter? center;
    private int publishScheduled;
    private (bool Eligible, bool Playing, string Title, string Artist)? published;

    public async Task BindAsync(ConsoleReceiveSession session, CancellationToken cancellationToken = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (ReferenceEquals(active, session)) return;
                if (active is not null)
                {
                    active.ControlStateInvalidated -= OnStateChanged;
                    active.WebStreamsChanged -= OnStateChanged;
                    active.ConnectionStatesChanged -= OnStateChanged;
                }
                active = session;
                active.ControlStateInvalidated += OnStateChanged;
                active.WebStreamsChanged += OnStateChanged;
                active.ConnectionStatesChanged += OnStateChanged;
                published = null;
                InitializeCommands();
            }
            PublishState();
        });
    }

    public async Task UnbindAsync(ConsoleReceiveSession session)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (sync)
            {
                if (!ReferenceEquals(active, session)) return;
                active.ControlStateInvalidated -= OnStateChanged;
                active.WebStreamsChanged -= OnStateChanged;
                active.ConnectionStatesChanged -= OnStateChanged;
                active = null;
            }
            PublishState();
        });
    }

    private void InitializeCommands()
    {
        if (center is not null) return;
        center = MPRemoteCommandCenter.Shared;
        foreach (var command in new[] { center.NextTrackCommand, center.PreviousTrackCommand,
            center.SeekForwardCommand, center.SeekBackwardCommand, center.SkipForwardCommand,
            center.SkipBackwardCommand, center.ChangePlaybackPositionCommand,
            center.ChangePlaybackRateCommand, center.ChangeRepeatModeCommand, center.ChangeShuffleModeCommand,
            center.BookmarkCommand, center.LikeCommand, center.DislikeCommand, center.RatingCommand,
            center.EnableLanguageOptionCommand, center.DisableLanguageOptionCommand })
            command.Enabled = false;
        Register(center.PlayCommand, _ => RequestPlay());
        Register(center.PauseCommand, _ => Pause());
        Register(center.StopCommand, _ => Pause());
        Register(center.TogglePlayPauseCommand, _ => IsPlaying ? Pause() : RequestPlay());
    }

    private void Register(MPRemoteCommand command, Func<MPRemoteCommandEvent, MPRemoteCommandHandlerStatus> handler)
        => handlers.Add((command, command.AddTarget(handler)));

    private bool IsPlaying { get { lock (sync) return active?.Execution.Snapshot.CanReceive == true; } }
    private static bool IsEligible(ConsoleReceiveSession? session)
        => session is not null && session.IsAcceptingCommands &&
            (session.ConnectionStates.Any(connection => connection.State is
                 RadioConnectionState.Starting or RadioConnectionState.WaitingForLogin or
                 RadioConnectionState.Authenticating or RadioConnectionState.Configuring or RadioConnectionState.Connected) ||
             session.WebStreams.Any(stream => stream.Selected && !stream.Playback.IsFailed));

    internal Task<bool> ResumeOnForegroundAsync()
    {
        // Returning to the console renews listening intent, but cannot recover
        // an interruption that iOS has not ended or replay an old transmit lease.
        lock (sync)
        {
            if (active is null || !IsEligible(active) || resuming is not null ||
                active.Execution.Snapshot.State != ConsoleExecutionState.RequiresResume)
                return Task.FromResult(false);
            resuming = active;
            return ResumeAsync(active);
        }
    }

    private MPRemoteCommandHandlerStatus RequestPlay()
    {
        lock (sync)
        {
            if (!IsEligible(active) || resuming is not null) return MPRemoteCommandHandlerStatus.CommandFailed;
            resuming = active;
            _ = ResumeAsync(active!);
            return MPRemoteCommandHandlerStatus.Success;
        }
    }

    internal async Task<bool> ResumeAsync(ConsoleReceiveSession session)
    {
        try
        {
            lock (sync) if (!ReferenceEquals(active, session) || !IsEligible(session)) return false;
            await session.ResumeAudioAsync(audio.ResumeListeningAsync).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException exception)
        {
            Console.Error.WriteLine($"Remote listening resume canceled: {exception.Message}");
            return false;
        }
        catch (ObjectDisposedException) { return false; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Remote listening resume failed: {exception.Message}");
            return false;
        }
        finally
        {
            lock (sync) if (ReferenceEquals(resuming, session)) resuming = null;
            SchedulePublish();
        }
    }

    internal MPRemoteCommandHandlerStatus Pause()
    {
        lock (sync)
        {
            if (!IsEligible(active)) return MPRemoteCommandHandlerStatus.CommandFailed;
            // Close session admission before stopping the physical output. A late
            // resume cannot release audio across this generation change.
            active!.SetAudioAvailable(false, "Listening paused. Resume from playback controls or Settings.", requiresExplicitResume: true);
            audio.StopImmediately();
        }
        SchedulePublish();
        return MPRemoteCommandHandlerStatus.Success;
    }

    private void OnStateChanged(object? sender, EventArgs args) => SchedulePublish();

    private void SchedulePublish()
    {
        if (Interlocked.Exchange(ref publishScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref publishScheduled, 0);
            PublishState();
        });
    }

    private void PublishState()
    {
        lock (sync)
        {
            if (center is null) return;
            bool eligible = IsEligible(active);
            bool playing = eligible && active!.Execution.Snapshot.CanReceive;
            audio.SetListeningRequired(eligible || active?.HasRecordingPlayback == true);
            string title = "DVM Console NEO";
            string artist = playing ? "Listening" : "Paused";
            if (playing)
            {
                var snapshot = active!.CaptureSnapshot();
                // Configuration order provides a stable choice when several channels receive.
                foreach (ChannelDescriptor channel in active.CaptureTopology().Channels)
                {
                    if (!snapshot.Channels.TryGetValue(channel.Id, out var state) ||
                        !state.ReceiveActive || !state.ReceiveEnabled) continue;
                    title = channel.Name;
                    artist = state.ReceiveSourceId is uint source ? $"RID {source}" : "Receiving";
                    if (!string.IsNullOrWhiteSpace(state.LastCaller) && state.LastCaller != "--" &&
                        state.LastCaller != state.ReceiveSourceId?.ToString())
                        artist += $" · {state.LastCaller}";
                    break;
                }
            }
            if (published == (eligible, playing, title, artist)) return;
            published = (eligible, playing, title, artist);
            center.PlayCommand.Enabled = center.PauseCommand.Enabled = center.StopCommand.Enabled =
                center.TogglePlayPauseCommand.Enabled = eligible;
            // Apple clears Now Playing with nil; this binding's setter is
            // annotated non-null despite that native reset contract.
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = eligible
                ? new MPNowPlayingInfo { Title = title, Artist = artist, AlbumTitle = "DVM Console NEO",
                    IsLiveStream = true, MediaType = MPNowPlayingInfoMediaType.Audio,
                    PlaybackRate = playing ? 1 : 0, DefaultPlaybackRate = 1 }
                : null!;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (sync)
            {
                if (active is not null)
                {
                    active.ControlStateInvalidated -= OnStateChanged;
                    active.WebStreamsChanged -= OnStateChanged;
                    active.ConnectionStatesChanged -= OnStateChanged;
                }
                active = null;
                PublishState();
                foreach (var (command, target) in handlers) { command.RemoveTarget(target); target.Dispose(); }
                handlers.Clear();
                center = null;
            }
        });
    }
}
