// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession
    {
        private RecordingPlaybackCoordinator? observedPlayback;
        public RecordingPlaybackChannelState PlaybackState => Runtime.RecordingPlaybackState;

        private void BindRecordingPlaybackState()
        {
            if (observedPlayback is not null) return;
            observedPlayback = RecordingPlayback!;
            observedPlayback.PlaybackStateChanged += PlaybackStateChanged;
        }

        public void UnbindRecordingPlaybackState()
        {
            if (observedPlayback is null) return;
            observedPlayback.PlaybackStateChanged -= PlaybackStateChanged;
            observedPlayback = null;
        }

        private void PlaybackStateChanged(object? sender, RecordingPlaybackStateChangedEventArgs args)
        {
            ChannelId? channel = args.IsPlaying && args.Identity is { } identity
                ? RecordingChannelResolver.Find(State!.Topology.Channels.Select(channel =>
                    Runtime.Channels[channel.Id].Runtime.Definition), identity)
                : null;
            var snapshot = PlaybackState.Apply(args.RecordingId, args.IsPlaying, channel);
            Presentation?.PresentRecordingPlaybackStateChanged(args, snapshot);
        }
    }
}
