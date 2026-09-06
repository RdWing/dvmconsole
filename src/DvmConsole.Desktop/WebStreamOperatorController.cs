// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal interface IWebStreamOperatorSession
{
    bool NetworkAccessDisabled { get; }
    ValueTask InvokeUiAsync(Action action);
    void PersistSettings();
    void PublishAudioStatus(string text);
}

internal sealed class WebStreamOperatorSessionPort(
    Func<bool> networkAccessDisabled,
    Func<Action, ValueTask> invokeUi,
    Action persistSettings,
    Action<string> publishAudioStatus) : IWebStreamOperatorSession
{
    public bool NetworkAccessDisabled => networkAccessDisabled();
    public ValueTask InvokeUiAsync(Action action) => invokeUi(action);
    public void PersistSettings() => persistSettings();
    public void PublishAudioStatus(string text) => publishAudioStatus(text);
}

/// <summary>
/// Owns configured web-stream presentation, operator start/stop commands, and
/// configuration-scoped persistence behind the main-window binding facade.
/// </summary>
internal sealed class WebStreamOperatorController : IAsyncDisposable
{
    private readonly UserSettings settings;
    private readonly string configurationIdentity;
    private readonly WebStreamPlaybackCoordinator playback;
    private readonly IWebStreamOperatorSession session;
    private readonly ObservableCollection<WebStreamViewModel> streams = [];
    private readonly AsyncDisposal disposal = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object restoreSync = new();
    private Task? restoreTask;
    private int disposalStarted;

    public WebStreamOperatorController(
        UserSettings settings,
        string configurationIdentity,
        WebStreamPlaybackCoordinator playback,
        IWebStreamOperatorSession session)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.configurationIdentity = configurationIdentity ?? string.Empty;
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Streams = new ReadOnlyObservableCollection<WebStreamViewModel>(streams);
    }

    public ReadOnlyObservableCollection<WebStreamViewModel> Streams { get; }
    public IReadOnlyList<WebStreamViewModel> ActiveStreams => playback.ActiveStreams;

    public void Initialize(
        IEnumerable<WebStreamViewModel> configuredStreams,
        IReadOnlyList<AudioDeviceOptionViewModel> outputDevices)
    {
        ArgumentNullException.ThrowIfNull(configuredStreams);
        ArgumentNullException.ThrowIfNull(outputDevices);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);

        foreach (WebStreamViewModel stream in configuredStreams)
        {
            stream.SetOutputDeviceOptions(outputDevices);
            stream.SetInitialVolume(
                settings.WebStreamVolumes.TryGetValue(stream.Name, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            stream.RestoreOutputDeviceId(GetOutputDeviceId(stream) ?? string.Empty);
            stream.VolumeChanged += HandleVolumeChanged;
            stream.PropertyChanged += HandlePropertyChanged;
            stream.Configure(StartAsync, StopAsync);
            streams.Add(stream);
        }
    }

    public bool IsActive(WebStreamViewModel stream) => playback.IsActive(stream);
    public void SetVolume(WebStreamViewModel stream, double volume) => playback.SetVolume(stream, volume);
    public Task StartPlaybackAsync(WebStreamViewModel stream) => playback.StartAsync(stream);
    public Task StopPlaybackAsync(WebStreamViewModel stream) => playback.StopAsync(stream);
    public Task ResetAudioBackendAsync() => playback.ResetAudioBackendAsync();

    public Task RestoreSelectedForSessionAsync()
    {
        lock (restoreSync)
        {
            if (Volatile.Read(ref disposalStarted) != 0)
                return Task.CompletedTask;
            return restoreTask ??= RestoreSelectedAsync(lifetime.Token);
        }
    }

    public bool SaveOutputDevice(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string deviceId = stream.OutputDeviceIdText.Trim();
        if (deviceId.Length > 256)
        {
            session.PublishAudioStatus("Output device IDs must be 256 characters or fewer.");
            return false;
        }

        if (deviceId.Length == 0 || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            settings.WebStreamOutputDeviceIds.Remove(stream.Name);
        else
            settings.WebStreamOutputDeviceIds[stream.Name] = deviceId;

        session.PersistSettings();
        stream.RestoreOutputDeviceId(deviceId);
        session.PublishAudioStatus(stream.IsActive
            ? $"Output route saved for {stream.Name}; stop and start it again to apply the route."
            : $"Output route saved for {stream.Name}.");
        return true;
    }

    public string? GetOutputDeviceId(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return settings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? deviceId)
            ? deviceId
            : null;
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposalStarted, 1);
        lifetime.Cancel();

        foreach (WebStreamViewModel stream in streams)
        {
            stream.VolumeChanged -= HandleVolumeChanged;
            stream.PropertyChanged -= HandlePropertyChanged;
        }

        Task? pendingRestore;
        lock (restoreSync)
            pendingRestore = restoreTask;
        var cleanup = new AsyncCleanup();
        if (pendingRestore is not null)
        {
            await cleanup.RunTaskAsync(async () =>
            {
                try
                {
                    await pendingRestore.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }).ConfigureAwait(false);
        }

        try
        {
            await cleanup.RunTaskAsync(() => playback.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
        }
        cleanup.ThrowIfFailed();
    }

    private async Task StartAsync(WebStreamViewModel stream)
        => await StartAsync(stream, CancellationToken.None).ConfigureAwait(false);

    private async Task StartAsync(
        WebStreamViewModel stream,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposalStarted) != 0)
            return;

        if (session.NetworkAccessDisabled)
        {
            await session.InvokeUiAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, false, "Demo offline");
                session.PublishAudioStatus("Demo safety boundary: web-stream network access is disabled.");
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            await playback.StartAsync(stream, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            PersistSelectedState(stream);
            await session.InvokeUiAsync(() =>
                session.PublishAudioStatus($"Web stream {stream.Name}: {stream.StatusText}"))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            await session.InvokeUiAsync(() =>
                stream.SetPlaybackState(false, false, false, false, "Off"))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            await session.InvokeUiAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, true, $"Failed: {exception.Message}");
                session.PublishAudioStatus($"Web stream {stream.Name}: {stream.StatusText}");
            }).ConfigureAwait(false);
        }
    }

    private async Task StopAsync(WebStreamViewModel stream)
    {
        if (Volatile.Read(ref disposalStarted) != 0)
            return;

        try
        {
            await playback.StopAsync(stream).ConfigureAwait(false);
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            PersistSelectedState(stream);
            await session.InvokeUiAsync(() =>
                session.PublishAudioStatus($"Web stream {stream.Name}: Off"))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            await session.InvokeUiAsync(() =>
                stream.SetPlaybackState(false, false, false, false, "Off"))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref disposalStarted) != 0)
                return;
            await session.InvokeUiAsync(() =>
            {
                stream.SetPlaybackState(false, false, false, true, $"Failed to stop: {exception.Message}");
                session.PublishAudioStatus($"Web stream {stream.Name}: {stream.StatusText}");
            }).ConfigureAwait(false);
        }
    }

    private void HandleVolumeChanged(object? sender, double volume)
    {
        if (Volatile.Read(ref disposalStarted) != 0 || sender is not WebStreamViewModel stream)
            return;

        settings.WebStreamVolumes[stream.Name] = volume;
        playback.SetVolume(stream, volume);
        session.PersistSettings();
    }

    private void HandlePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposalStarted) == 0 &&
            args.PropertyName == nameof(WebStreamViewModel.IsActive) &&
            sender is WebStreamViewModel stream)
            PersistSelectedState(stream);
    }

    private async Task RestoreSelectedAsync(CancellationToken cancellationToken)
    {
        if (!settings.RestoreSelectedChannelsOnStartup || settings.SelectedWebStreams.Count == 0)
            return;

        foreach (WebStreamViewModel stream in streams.Where(stream =>
            WebStreamSelectionIdentity.IsAuthorized(
                settings.SelectedWebStreams,
                configurationIdentity,
                stream)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StartAsync(stream, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void PersistSelectedState(WebStreamViewModel stream)
    {
        if (!settings.RestoreSelectedChannelsOnStartup)
            return;

        HashSet<string> selectedIdentities = settings.SelectedWebStreams
            .Where(WebStreamSelectionIdentity.IsVersioned)
            .ToHashSet(StringComparer.Ordinal);
        string identity = WebStreamSelectionIdentity.Create(configurationIdentity, stream);
        if (stream.IsActive && !stream.IsFailed)
        {
            if (identity.Length > 0)
                selectedIdentities.Add(identity);
        }
        else
        {
            selectedIdentities.Remove(identity);
        }
        settings.SelectedWebStreams = selectedIdentities.ToList();
        session.PersistSettings();
    }
}
