// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public interface IConsoleConnectionCueSettings
{
    bool ConnectionChimes { get; }
    bool CanSaveConnectionChimes { get; }
    ValueTask SetConnectionChimesAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IConsoleConnectionCuePreferences
{
    ValueTask<bool> LoadConnectionChimesAsync(CancellationToken cancellationToken = default);
    ValueTask SaveConnectionChimesAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed partial class ConsoleReceiveSession : IConsoleConnectionCueSettings
{
    private ConnectionCueWorker connectionCues = null!;
    private bool connectionChimes;
    public bool ConnectionChimes => Volatile.Read(ref connectionChimes);
    public bool CanSaveConnectionChimes => !IsStopping && dependencies.Preferences is IConsoleConnectionCuePreferences;

    private void InitializeConnectionCues()
    {
        // Minimal receive hosts need not supply lifecycle/output services for
        // an optional feature they do not expose through preferences.
        if (dependencies.Preferences is not IConsoleConnectionCuePreferences) return;
        var host = dependencies.Host;
        var player = services.Audio.OwnAsync("connection-cue-player", new LocalTonePlayer(
            () => host.AudioBackends.Create(AudioBackendConfiguration.Default), () => null,
            openSharedOutput: token => receive.Audio.OpenMonitorOutputAsync(cancellationToken: token)));
        connectionCues = services.Audio.OwnAsync("connection-cue-worker", new ConnectionCueWorker(
            () => !IsStopping && ConnectionChimes && host.Lifecycle.IsActive && state.Execution.Snapshot.CanSendTones,
            async (connected, token) => await player.PlayAsync(connected
                ? LocalToneCues.ConnectionEstablished : LocalToneCues.ConnectionLost, token).ConfigureAwait(false),
            exception => ReportMediaFailure("Connection chime", exception)));
        host.Lifecycle.Deactivated += OnConnectionCueBackground;
        services.Presentation.Register("connection-cue-lifecycle", () =>
        {
            host.Lifecycle.Deactivated -= OnConnectionCueBackground;
            return ValueTask.CompletedTask;
        });
    }

    private void OnConnectionCueBackground(object? sender, EventArgs args) => connectionCues.Cancel();

    private async ValueTask RestoreConnectionCuesAsync(CancellationToken token)
    {
        if (dependencies.Preferences is IConsoleConnectionCuePreferences preferences)
            Volatile.Write(ref connectionChimes, await preferences.LoadConnectionChimesAsync(token).ConfigureAwait(false));
    }

    public ValueTask SetConnectionChimesAsync(bool enabled, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            var preferences = dependencies.Preferences as IConsoleConnectionCuePreferences
                ?? throw new NotSupportedException("Connection chime settings are unavailable.");
            await preferences.SaveConnectionChimesAsync(enabled, token).ConfigureAwait(false);
            Volatile.Write(ref connectionChimes, enabled);
            if (!enabled) connectionCues.Cancel();
            SetStatus(enabled ? "Connection chimes enabled." : "Connection chimes disabled.");
        }, cancellationToken);
}
