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

        IEnumerable<string> deviceIdentities = devices
            .Select(device => $"{device.Id}\u001f{device.Name}")
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase);
        return string.Join('\u001e', new[] { defaultIdentity }.Concat(deviceIdentities));
    }
}

// Polling provides one portable lifecycle for CoreAudio and Windows endpoints.
// The callback is awaited so route rebuilds never overlap or reorder.
internal sealed class DefaultAudioDeviceMonitor : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private readonly IAudioDeviceTopologyProvider topologyProvider;
    private readonly Func<AudioDeviceTopologyChange, CancellationToken, Task> changeHandler;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task monitorTask = Task.CompletedTask;
    private AudioDeviceTopology? previousTopology;
    private bool disposed;

    public DefaultAudioDeviceMonitor(
        IAudioDeviceTopologyProvider topologyProvider,
        Func<AudioDeviceTopologyChange, CancellationToken, Task> changeHandler,
        TimeSpan? pollInterval = null)
    {
        this.topologyProvider = topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
        this.changeHandler = changeHandler ?? throw new ArgumentNullException(nameof(changeHandler));
        this.pollInterval = pollInterval ?? DefaultPollInterval;
        if (this.pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellation is not null)
            return;

        cancellation = new CancellationTokenSource();
        monitorTask = MonitorAsync(cancellation.Token);
    }

    internal async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AudioDeviceTopology current = topologyProvider.Read();
            AudioDeviceTopology? previous = previousTopology;
            if (previous is null)
            {
                previousTopology = current;
                return;
            }
            if (previous == current)
                return;

            await changeHandler(
                new AudioDeviceTopologyChange(
                    InputChanged: previous.InputSignature != current.InputSignature,
                    OutputChanged: previous.OutputSignature != current.OutputSignature),
                cancellationToken).ConfigureAwait(false);
            previousTopology = current;
        }
        finally
        {
            checkGate.Release();
        }
    }

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
            currentCancellation?.Dispose();
            checkGate.Dispose();
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
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
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
