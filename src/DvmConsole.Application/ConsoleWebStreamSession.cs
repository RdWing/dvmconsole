// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed record ConsoleWebStreamSnapshot(WebStreamId Id, string Name, double Volume,
    bool Selected, WebStreamPlaybackState Playback);

public interface IConsoleWebStreamCommands
{
    ImmutableArray<ConsoleWebStreamSnapshot> WebStreams { get; }
    event EventHandler? WebStreamsChanged;
    Task SetWebStreamPlayingAsync(WebStreamId id, bool playing, CancellationToken cancellationToken = default);
    Task SetWebStreamVolumeAsync(WebStreamId id, double volume, CancellationToken cancellationToken = default);
}

/// <summary>Owns stream intent and interruption recovery around the shared playback coordinator.</summary>
internal sealed class ConsoleWebStreamSession : IConsoleWebStreamCommands, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ImmutableDictionary<WebStreamId, WebStreamPlaybackDescriptor> definitions;
    private readonly IConsoleWebStreamPreferences? preferences;
    private readonly WebStreamPlaybackCoordinator playback;
    private readonly Dictionary<WebStreamId, CancellationTokenSource> starts = [];
    private readonly Dictionary<WebStreamId, long> intents = [];
    private readonly HashSet<WebStreamId> stoppedByOperator = [];
    private readonly AsyncDisposal disposal = new();
    private CancellationTokenSource admission = new();
    private ImmutableArray<ConsoleWebStreamSnapshot> snapshots;
    private Task pause = Task.CompletedTask;
    private readonly bool ownsPlayback;
    private bool available;
    private bool disposed;
    private long generation;

    public ConsoleWebStreamSession(IEnumerable<WebStreamPlaybackDescriptor> streams,
        Func<IAudioBackend> createAudioBackend, IConsoleWebStreamPreferences? preferences,
        Func<WebStreamPlaybackDescriptor, CancellationToken, Task<Stream>>? openStream = null,
        Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>>? createDecoder = null,
        Func<CancellationToken, ValueTask<IAudioPlayback>>? openSharedOutput = null)
        : this(streams, preferences, observer => new WebStreamPlaybackCoordinator(
            createAudioBackend, () => "default", openStream, createDecoder, observer, openSharedOutput), ownsPlayback: true)
    {
    }

    internal ConsoleWebStreamSession(IEnumerable<WebStreamPlaybackDescriptor> streams,
        IConsoleWebStreamPreferences? preferences,
        Func<Func<WebStreamPlaybackState, ValueTask>, WebStreamPlaybackCoordinator> createRuntime,
        bool ownsPlayback = false)
    {
        var ordered = streams.ToArray();
        definitions = ordered.ToImmutableDictionary(stream => stream.Id);
        this.preferences = preferences;
        this.ownsPlayback = ownsPlayback;
        snapshots = ordered.Select(stream => new ConsoleWebStreamSnapshot(stream.Id, stream.Name, stream.Volume,
            false, new(stream.Id, false, false, false, false, "Off"))).ToImmutableArray();
        playback = createRuntime(state =>
        {
            Update(state.Id, item => item with { Playback = state });
            return ValueTask.CompletedTask;
        });
    }

    public ImmutableArray<ConsoleWebStreamSnapshot> WebStreams { get { lock (sync) return snapshots; } }
    public event EventHandler? WebStreamsChanged;

    public async Task PrepareAsync(CancellationToken token)
    {
        if (preferences is null) return;
        var saved = await preferences.LoadWebStreamsAsync(definitions.Values.ToArray(), token).ConfigureAwait(false);
        foreach (var (id, value) in saved)
            Update(id, item => item with { Volume = value.Volume, Selected = value.RestoreSelected });
    }

    public async Task SetWebStreamPlayingAsync(WebStreamId id, bool playing, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!definitions.ContainsKey(id)) throw new ArgumentException("Unknown web stream.", nameof(id));
        long intent;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            intent = intents.GetValueOrDefault(id) + 1;
            intents[id] = intent;
            if (playing) stoppedByOperator.Remove(id);
            else stoppedByOperator.Add(id);
            if (starts.TryGetValue(id, out var pending)) pending.Cancel();
        }
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (intents[id] != intent) return;
            }
            if (preferences is not null)
                await preferences.SaveWebStreamAsync(definitions[id], selected: playing, cancellationToken: cancellationToken).ConfigureAwait(false);
            Update(id, item => item with { Selected = playing });
            if (playing) await StartAsync(id, cancellationToken, intent).ConfigureAwait(false);
            else await playback.StopAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    public async Task SetWebStreamVolumeAsync(WebStreamId id, double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > 4) throw new ArgumentOutOfRangeException(nameof(volume));
        var definition = definitions[id];
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
            if (preferences is not null)
                await preferences.SaveWebStreamAsync(definition, volume: volume, cancellationToken: cancellationToken).ConfigureAwait(false);
            playback.SetVolume(id, volume);
            Update(id, item => item with { Volume = volume });
        }
        finally { commands.Release(); }
    }

    private async Task StartAsync(WebStreamId id, CancellationToken token, long? intent = null)
    {
        CancellationTokenSource pending;
        double volume;
        lock (sync)
        {
            if (!available || disposed) return;
            if (stoppedByOperator.Contains(id)) return;
            if (intent.HasValue && intents.GetValueOrDefault(id) != intent.Value) return;
            pending = CancellationTokenSource.CreateLinkedTokenSource(token, admission.Token);
            starts[id] = pending;
            volume = snapshots.First(item => item.Id == id).Volume;
        }
        try { await playback.StartAsync(definitions[id] with { Volume = volume, OutputDeviceId = null }, pending.Token).ConfigureAwait(false); }
        finally
        {
            lock (sync) { starts.Remove(id); pending.Dispose(); }
        }
    }

    // Close admission before awaiting any network or audio cleanup. Commands that
    // arrive while paused may edit intent but cannot construct new endpoints.
    public Task PauseAsync()
    {
        lock (sync)
        {
            generation++;
            if (!available) return pause;
            available = false;
            admission.Cancel();
            return pause = PauseCoreAsync();
        }
    }

    private async Task PauseCoreAsync()
    {
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(definitions.Keys.Select(id => playback.StopAsync(id))).ConfigureAwait(false);
            await playback.ResetAudioBackendAsync().ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        Task stopping;
        long requestedGeneration;
        lock (sync) { stopping = pause; requestedGeneration = generation; }
        await stopping.WaitAsync(token).ConfigureAwait(false);
        await commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (requestedGeneration != generation) throw new OperationCanceledException("A newer pause superseded web-stream recovery.");
                if (available) return;
                admission.Dispose();
                admission = new();
                available = true;
            }
            foreach (var stream in WebStreams.Where(stream => stream.Selected))
                await StartAsync(stream.Id, token).ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    private void Update(WebStreamId id, Func<ConsoleWebStreamSnapshot, ConsoleWebStreamSnapshot> change)
    {
        lock (sync)
        {
            if (disposed) return;
            int index = -1;
            for (int i = 0; i < snapshots.Length; i++)
                if (snapshots[i].Id == id) { index = i; break; }
            if (index < 0) return;
            snapshots = snapshots.SetItem(index, change(snapshots[index]));
        }
        foreach (EventHandler observer in WebStreamsChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Presentation cannot interrupt playback ownership. */ }
        }
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        Task stopping;
        lock (sync) { disposed = true; stopping = PauseAsync(); }
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(() => stopping).ConfigureAwait(false);
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ownsPlayback) await cleanup.RunTaskAsync(() => playback.DisposeAsync().AsTask()).ConfigureAwait(false);
            admission.Dispose();
        }
        finally { commands.Release(); }
        cleanup.ThrowIfFailed();
    });

    public async ValueTask FlushAsync(CancellationToken token)
    {
        await commands.WaitAsync(token).ConfigureAwait(false);
        commands.Release();
    }
}
