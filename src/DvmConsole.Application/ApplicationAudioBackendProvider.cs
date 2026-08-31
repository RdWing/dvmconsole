using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Application;

internal sealed record ApplicationAudioConfiguration(
    AudioProcessingMode ProcessingMode,
    string InputDeviceId,
    string OutputDeviceId);

// Creates audio backends for one configured application route. Apple Voice
// Processing I/O is full-duplex, so every playback source must feed one shared
// mixer instead of opening a competing CoreAudio output unit.
internal sealed class ApplicationAudioBackendProvider : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<ApplicationAudioConfiguration, IAudioBackend> createNativeBackend;
    private ApplicationAudioConfiguration configuration;
    private SharedAudioOutputRouter? sharedOutput;
    private bool disposed;

    public ApplicationAudioBackendProvider(
        ApplicationAudioConfiguration configuration,
        Func<ApplicationAudioConfiguration, IAudioBackend> createNativeBackend)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.createNativeBackend = createNativeBackend ?? throw new ArgumentNullException(nameof(createNativeBackend));
    }

    public IAudioBackend CreateBackend()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ApplicationAudioConfiguration current = configuration;
            IAudioBackend backend = createNativeBackend(current);
            if (current.ProcessingMode != AudioProcessingMode.AppleVoiceProcessing)
                return backend;

            sharedOutput ??= new SharedAudioOutputRouter(
                () => createNativeBackend(current));
            return new SharedOutputAudioBackend(backend, sharedOutput);
        }
    }

    public async Task ReconfigureAsync(ApplicationAudioConfiguration next)
    {
        ArgumentNullException.ThrowIfNull(next);
        SharedAudioOutputRouter? previousOutput;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration == next)
                return;

            previousOutput = sharedOutput;
            sharedOutput = null;
            configuration = next;
        }

        if (previousOutput is not null)
            await previousOutput.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        SharedAudioOutputRouter? output;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            output = sharedOutput;
            sharedOutput = null;
        }

        if (output is not null)
            await output.DisposeAsync().ConfigureAwait(false);
    }
}

// Routes independent playback clients into one physical stream. Each caller
// receives an isolated mixer lane and retains normal IAudioPlayback ownership.
internal sealed class SharedAudioOutputRouter : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<IAudioBackend> createBackend;
    private readonly Dictionary<string, SharedAudioOutputRoute> routes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public SharedAudioOutputRouter(Func<IAudioBackend> createBackend)
    {
        this.createBackend = createBackend ?? throw new ArgumentNullException(nameof(createBackend));
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);
        if (format != PcmAudioFormat.Voice8KhzMono16Bit)
        {
            throw new NotSupportedException(
                "The shared Apple voice output currently supports 8 kHz mono 16-bit PCM.");
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!routes.TryGetValue(device.Id, out SharedAudioOutputRoute? route))
            {
                route = CreateRoute(device);
                routes.Add(device.Id, route);
            }
            return route.Mixer.OpenChannel($"application playback on {device.Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        SharedAudioOutputRoute[] oldRoutes;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            oldRoutes = routes.Values.ToArray();
            routes.Clear();
        }

        List<Exception>? failures = null;
        foreach (SharedAudioOutputRoute route in oldRoutes)
        {
            try
            {
                await DisposeRouteAsync(route).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("Multiple shared audio routes failed to close.", failures);
    }

    private SharedAudioOutputRoute CreateRoute(AudioDeviceInfo device)
    {
        IAudioBackend? backend = null;
        IAudioPlayback? playback = null;
        try
        {
            backend = createBackend();
            playback = backend.OpenPlayback(device, PcmAudioFormat.Voice8KhzMono16Bit);
            var route = new SharedAudioOutputRoute(backend, new AudioMixer(playback));
            route.Mixer.Faulted += _ => RetireFailedRoute(device.Id, route);
            backend = null;
            playback = null;
            return route;
        }
        finally
        {
            if (playback is not null)
                Observe(playback.DisposeAsync().AsTask());
            backend?.Dispose();
        }
    }

    private void RetireFailedRoute(string deviceId, SharedAudioOutputRoute failedRoute)
    {
        lock (sync)
        {
            if (!routes.TryGetValue(deviceId, out SharedAudioOutputRoute? current) ||
                !ReferenceEquals(current, failedRoute))
            {
                return;
            }
            routes.Remove(deviceId);
        }

        // Existing lanes already carry the mixer failure. Retire their native
        // route in the background so the next playback open can create a clean
        // physical mixer immediately.
        Observe(DisposeRouteAsync(failedRoute));
    }

    private static async Task DisposeRouteAsync(SharedAudioOutputRoute route)
    {
        try
        {
            await route.Mixer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            route.Backend.Dispose();
        }
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed record SharedAudioOutputRoute(IAudioBackend Backend, AudioMixer Mixer);
}

internal sealed class SharedOutputAudioBackend :
    IAudioBackend,
    IDefaultAudioDeviceIdentityProvider
{
    private readonly IAudioBackend inner;
    private readonly SharedAudioOutputRouter sharedOutput;

    public SharedOutputAudioBackend(
        IAudioBackend inner,
        SharedAudioOutputRouter sharedOutput)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.sharedOutput = sharedOutput ?? throw new ArgumentNullException(nameof(sharedOutput));
    }

    public string Name => inner.Name;

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        => inner.EnumerateDevices(direction);

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
        => (inner as IDefaultAudioDeviceIdentityProvider)?.GetDefaultDeviceIdentity(direction);

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
        => inner.OpenCapture(device, format);

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        => sharedOutput.OpenPlayback(device, format);

    public void Dispose() => inner.Dispose();
}
