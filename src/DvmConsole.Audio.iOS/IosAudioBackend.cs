// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

public sealed class IosAudioBackend(IosAudioSessionOwner owner) : IAudioBackend, IDefaultAudioDeviceIdentityProvider
{
    private readonly IosAudioSessionOwner owner = owner ?? throw new ArgumentNullException(nameof(owner));
    private readonly object sync = new();
    private readonly List<IAsyncDisposable> endpoints = [];
    private bool disposed;
    public string Name => "iOS RemoteIO";
    public string? GetDefaultDeviceIdentity(AudioDirection direction) => owner.GetRouteIdentity(direction);
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        => direction == AudioDirection.Output
            ? [new("ios-system", string.IsNullOrWhiteSpace(owner.OutputName) ? "System audio route" : owner.OutputName, direction, true, owner.IsBluetoothRoute(direction))]
            : [new("ios-system", "System microphone", direction, true, owner.IsBluetoothRoute(direction))];

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        Validate(device, format, AudioDirection.Input);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var capture = new IosAudioCapture(owner, format, Retired);
            endpoints.Add(capture);
            return capture;
        }
    }
    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        Validate(device, format, AudioDirection.Output);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var playback = new IosAudioPlayback(owner, format, Retired);
            endpoints.Add(playback);
            return playback;
        }
    }
    private void Retired(IAsyncDisposable endpoint) { lock (sync) endpoints.Remove(endpoint); }

    private static void Validate(AudioDeviceInfo device, PcmAudioFormat format, AudioDirection direction)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);
        if (device.Direction != direction || device.Id != "ios-system")
            throw new ArgumentException("iOS uses the system audio route.", nameof(device));
        if (format.SampleRate != 8000 || format.BitsPerSample != 16 ||
            format.Channels is < 1 or > 2 || (direction == AudioDirection.Input && format.Channels != 1))
            throw new NotSupportedException("Console audio uses 8 kHz Int16 PCM; microphone input is mono.");
    }
    public void Dispose()
    {
        IAsyncDisposable[] owned;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            owned = endpoints.ToArray();
            endpoints.Clear();
        }
        List<Exception>? failures = null;
        foreach (IAsyncDisposable endpoint in owned.Reverse())
        {
            try { endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) throw new AggregateException("iOS audio endpoint cleanup failed.", failures);
    }
}

public sealed class IosAudioBackendFactory(IosAudioSessionOwner owner) : IAudioBackendFactory, IAudioDeviceChangeSourceFactory
{
    public IAudioDeviceChangeSource CreateDeviceChangeSource() => new IosAudioDeviceChanges(owner);

    public IAudioBackend Create(AudioBackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ProcessingMode != AudioProcessingMode.DvmConsole)
            throw new NotSupportedException("iOS uses Console audio processing.");
        // Imported desktop route IDs remain in settings; this backend always
        // exposes the one iOS-managed route rather than interpreting those IDs.
        return new IosAudioBackend(owner);
    }
}

internal sealed class IosAudioDeviceChanges(IosAudioSessionOwner owner) : IAudioDeviceChangeSource
{
    private bool started;
    public event EventHandler? Changed;
    public void Start()
    {
        if (started) return;
        started = true;
        owner.Changed += HandleChange;
    }
    private void HandleChange(object? sender, IosAudioSessionChange change) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        if (!started) return;
        started = false;
        owner.Changed -= HandleChange;
    }
}
