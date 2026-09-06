// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal sealed record AudioDeviceTopology(
    string InputSignature,
    string OutputSignature);

internal sealed record AudioDeviceTopologyChange(
    bool InputChanged,
    bool OutputChanged);

internal interface IAudioDeviceTopologyProvider
{
    AudioDeviceTopology Read();
}

internal sealed class AudioBackendDeviceTopologyProvider : IAudioDeviceTopologyProvider
{
    private readonly Func<IAudioBackend> createAudioBackend;

    public AudioBackendDeviceTopologyProvider(Func<IAudioBackend> createAudioBackend)
    {
        this.createAudioBackend = createAudioBackend ??
            throw new ArgumentNullException(nameof(createAudioBackend));
    }

    public AudioDeviceTopology Read()
    {
        using IAudioBackend backend = createAudioBackend();
        IReadOnlyList<AudioDeviceInfo> inputs = backend.EnumerateDevices(AudioDirection.Input);
        IReadOnlyList<AudioDeviceInfo> outputs = backend.EnumerateDevices(AudioDirection.Output);
        return new AudioDeviceTopology(
            CreateSignature(backend, AudioDirection.Input, inputs),
            CreateSignature(backend, AudioDirection.Output, outputs));
    }

    private static string CreateSignature(
        IAudioBackend backend,
        AudioDirection direction,
        IReadOnlyList<AudioDeviceInfo> devices)
    {
        string? defaultIdentity = devices.FirstOrDefault(device =>
            device.IsDefault && !device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))?.Id;
        if (defaultIdentity is null && backend is IDefaultAudioDeviceIdentityProvider identityProvider)
            defaultIdentity = identityProvider.GetDefaultDeviceIdentity(direction);
        defaultIdentity ??= devices.FirstOrDefault(device => device.IsDefault)?.Id ?? string.Empty;

        var signatureParts = new string[devices.Count + 1];
        signatureParts[0] = defaultIdentity;
        for (int index = 0; index < devices.Count; index++)
        {
            AudioDeviceInfo device = devices[index];
            signatureParts[index + 1] = string.Concat(device.Id, "\u001f", device.Name);
        }
        Array.Sort(
            signatureParts,
            index: 1,
            length: devices.Count,
            StringComparer.OrdinalIgnoreCase);
        return string.Join('\u001e', signatureParts);
    }
}

// Polling provides one portable lifecycle for CoreAudio and Windows endpoints.
// Stable topologies back off to reduce idle device enumeration, while a change
// or transient failure restores the responsive base cadence.
internal sealed class DefaultAudioDeviceMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMaximumPollInterval = TimeSpan.FromSeconds(5);
    private readonly IAudioDeviceTopologyProvider topologyProvider;
    private readonly Func<AudioDeviceTopologyChange, CancellationToken, Task> changeHandler;
    private readonly IAudioDeviceChangeSource? changeSource;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan maximumPollInterval;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private CancellationTokenSource? cancellation;
    private Task monitorTask = Task.CompletedTask;
    private AudioDeviceTopology? previousTopology;
    private long currentPollIntervalTicks;
    private long platformChangeVersion;
    private long handledPlatformChangeVersion;
    private bool disposed;

    public DefaultAudioDeviceMonitor(
        IAudioDeviceTopologyProvider topologyProvider,
        Func<AudioDeviceTopologyChange, CancellationToken, Task> changeHandler,
        IAudioDeviceChangeSource? changeSource = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maximumPollInterval = null)
    {
        this.topologyProvider = topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
        this.changeHandler = changeHandler ?? throw new ArgumentNullException(nameof(changeHandler));
        this.changeSource = changeSource;
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        this.maximumPollInterval = maximumPollInterval ?? DefaultMaximumPollInterval;
        if (this.pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (this.maximumPollInterval < this.pollInterval)
            throw new ArgumentOutOfRangeException(nameof(maximumPollInterval));
        currentPollIntervalTicks = this.pollInterval.Ticks;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellation is not null)
            return;

        cancellation = new CancellationTokenSource();
        if (changeSource is not null)
        {
            changeSource.Changed += HandleDeviceChanged;
            try
            {
                changeSource.Start();
            }
            catch
            {
                changeSource.Changed -= HandleDeviceChanged;
                // Polling remains the portable fallback when the platform
                // notification service is temporarily unavailable.
            }
        }
        monitorTask = MonitorAsync(cancellation.Token);
    }

    internal async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            bool changed = await CheckTopologyAsync(cancellationToken).ConfigureAwait(false);
            long currentTicks = CurrentPollInterval.Ticks;
            long nextTicks = changed
                ? pollInterval.Ticks
                : currentTicks >= maximumPollInterval.Ticks / 2
                    ? maximumPollInterval.Ticks
                    : currentTicks * 2;
            TimeSpan next = TimeSpan.FromTicks(nextTicks);
            Interlocked.Exchange(ref currentPollIntervalTicks, next.Ticks);
        }
        catch
        {
            Interlocked.Exchange(ref currentPollIntervalTicks, pollInterval.Ticks);
            throw;
        }
    }

    internal TimeSpan CurrentPollInterval
        => TimeSpan.FromTicks(Interlocked.Read(ref currentPollIntervalTicks));

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        CancellationTokenSource? currentCancellation = cancellation;
        cancellation = null;
        currentCancellation?.Cancel();
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        finally
        {
            if (changeSource is not null)
            {
                changeSource.Changed -= HandleDeviceChanged;
                changeSource.Dispose();
            }
            currentCancellation?.Dispose();
            checkGate.Dispose();
            wakeSignal.Dispose();
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Device transitions can make enumeration briefly unavailable.
                // Keep the last good topology and retry on the next poll.
            }

            try
            {
                await wakeSignal.WaitAsync(CurrentPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void HandleDeviceChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Interlocked.Increment(ref platformChangeVersion);
        try
        {
            wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake already represents every coalesced platform event.
        }
    }

    private async Task<bool> CheckTopologyAsync(CancellationToken cancellationToken)
    {
        await checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long observedPlatformChangeVersion = Volatile.Read(ref platformChangeVersion);
            AudioDeviceTopology current = topologyProvider.Read();
            AudioDeviceTopology? previous = previousTopology;
            if (previous is null)
            {
                previousTopology = current;
                Volatile.Write(ref handledPlatformChangeVersion, observedPlatformChangeVersion);
                return true;
            }
            bool platformReportedChange = observedPlatformChangeVersion !=
                Volatile.Read(ref handledPlatformChangeVersion);
            if (previous == current && !platformReportedChange)
                return false;

            await changeHandler(
                new AudioDeviceTopologyChange(
                    InputChanged: platformReportedChange || previous.InputSignature != current.InputSignature,
                    OutputChanged: platformReportedChange || previous.OutputSignature != current.OutputSignature),
                cancellationToken).ConfigureAwait(false);
            previousTopology = current;
            Volatile.Write(ref handledPlatformChangeVersion, observedPlatformChangeVersion);
            return true;
        }
        finally
        {
            checkGate.Release();
        }
    }
}
