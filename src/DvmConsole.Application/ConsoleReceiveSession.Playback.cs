// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleRecordingPlaybackCommands
{
    private RecordingPlaybackCoordinator? recordingPlayback => operationalRuntime.RecordingPlayback;
    private CancellationTokenSource? recordingStartup;
    private RecordingPlaybackChannelState recordingPlaybackState => operationalRuntime.RecordingPlaybackState;

    private ConsoleLiveRecordingPlaybackPorts? PrepareRecordingPlayback()
    {
        if (dependencies.Host.Recordings is not { } store) return null;
        operationalRuntime.RegisterRecordingPlaybackOwnership("history-playback");
        return new(store, () => dependencies.Host.AudioBackends.Create(AudioBackendConfiguration.Default), () => null,
            exception => SetStatus($"Recording playback failed: {exception.Message}"),
            OpenSharedOutput: token => receive.Audio.OpenMonitorOutputAsync(cancellationToken: token));
    }

    private void AttachRecordingPlayback()
    {
        if (recordingPlayback is null) return;
        recordingPlayback!.PlaybackStateChanged += OnRecordingPlaybackChanged;
        services.Presentation.Register("recording-playback-observer", () =>
        {
            recordingPlayback!.PlaybackStateChanged -= OnRecordingPlaybackChanged;
            lock (ingressSync)
            {
                if (recordingPlaybackState.Snapshot.Channel is { } channel) snapshots.Invalidate(channel);
                recordingPlaybackState.Clear();
            }
            return ValueTask.CompletedTask;
        });
    }

    private void OnRecordingPlaybackChanged(object? sender, RecordingPlaybackStateChangedEventArgs playback)
    {
        lock (ingressSync)
        {
            ChannelId? channel = playback.IsPlaying && !IsStopping && playback.Identity is { } identity
                ? RecordingChannelResolver.Find(topology.Channels.Select(channel => state.Channels[channel.Id].Runtime.Definition), identity)
                : null;
            recordingPlaybackState.Apply(playback.RecordingId, playback.IsPlaying, channel);
            Changed();
        }
    }

    public bool HasRecordingPlayback { get { lock (ingressSync) return recordingStartup is not null || recordingPlayback?.IsPlaying() == true; } }

    public bool IsRecordingPlaying(RecordingId id) => recordingPlayback?.IsPlaying(id) == true;

    public ValueTask PlayRecordingAsync(RecordingId id, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            var playback = recordingPlayback ?? throw new NotSupportedException("Recording playback is unavailable.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            long generation;
            lock (ingressSync)
            {
                if (audioUnavailable) throw new InvalidOperationException("Resume listening before playing recordings.");
                generation = audioGeneration;
                recordingStartup = startup;
                Changed();
            }
            try { await playback.StartAsync(id, startup.Token).ConfigureAwait(false); }
            finally { lock (ingressSync) { recordingStartup = null; Changed(); } }
            bool superseded;
            lock (ingressSync) superseded = audioUnavailable || generation != audioGeneration || IsStopping;
            if (superseded)
            {
                await playback.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw new OperationCanceledException("Recording playback was interrupted during startup.");
            }
        }, cancellationToken);

    public ValueTask StopRecordingPlaybackAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(token => recordingPlayback?.StopAsync(token) ?? Task.CompletedTask, cancellationToken);
}
