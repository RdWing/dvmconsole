// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleWebStreamCommands
{
    private ConsoleWebStreamSession webStreams => operationalRuntime.WebSession!;
    private Task webPause = Task.CompletedTask;
    public ImmutableArray<ConsoleWebStreamSnapshot> WebStreams => webStreams.WebStreams;
    public event EventHandler? WebStreamsChanged
    {
        add => webStreams.WebStreamsChanged += value;
        remove => webStreams.WebStreamsChanged -= value;
    }

    private ConsoleLiveWebPlaybackPorts PrepareWebStreams(IReadOnlyList<WebStreamPlaybackDescriptor> definitions)
    {
        operationalRuntime.RegisterWebPlaybackOwnership("web-streams",
            () => operationalRuntime.WebSession?.DisposeAsync() ?? ValueTask.CompletedTask);
        return new(new(() => dependencies.Host.AudioBackends.Create(AudioBackendConfiguration.Default), () => "default",
            OpenSharedOutput: token => receive.Audio.OpenMonitorOutputAsync(cancellationToken: token)),
            SessionDefinitions: definitions, SessionPreferences: dependencies.Preferences as IConsoleWebStreamPreferences);
    }

    private Task ResumeWebStreamsAsync(CancellationToken token)
    {
        lock (ingressSync)
            return IsStopping || audioUnavailable ? Task.CompletedTask : webStreams.ResumeAsync(token);
    }

    public Task SetWebStreamPlayingAsync(WebStreamId id, bool playing, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsStopping, this);
        return webStreams.SetWebStreamPlayingAsync(id, playing, cancellationToken);
    }

    public Task SetWebStreamVolumeAsync(WebStreamId id, double volume, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsStopping, this);
        return webStreams.SetWebStreamVolumeAsync(id, volume, cancellationToken);
    }
}
