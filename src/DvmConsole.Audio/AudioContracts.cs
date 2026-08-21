namespace DvmConsole.Audio;

public enum AudioDirection
{
    Input,
    Output
}

// Portable policy selected by the operator. Apple platforms implement the
// second mode with their native full-duplex voice-processing stack; other
// platforms can expose only the DVM Console mode until they provide an
// equivalent backend.
public enum AudioProcessingMode
{
    DvmConsole,
    AppleVoiceProcessing
}

public enum HighQualityBluetoothAudioStatus
{
    Off,
    Unavailable,
    Requested,
    Active,
    Unsupported
}

public interface IHighQualityBluetoothAudioStatus
{
    HighQualityBluetoothAudioStatus HighQualityBluetoothStatus { get; }
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDirection Direction,
    bool IsDefault,
    bool? IsBluetooth = null);

public sealed record PcmAudioFormat
{
    public PcmAudioFormat(int sampleRate, int channels, int bitsPerSample)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    public static PcmAudioFormat Voice8KhzMono16Bit { get; } = new(8000, 1, 16);
    public static PcmAudioFormat Voice8KhzStereo16Bit { get; } = new(8000, 2, 16);
}

public sealed class PcmSamplesEventArgs(ReadOnlyMemory<short> samples) : EventArgs
{
    public ReadOnlyMemory<short> Samples { get; } = samples;
}

public interface IAudioBackend : IDisposable
{
    string Name { get; }
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction);
    IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format);
    IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format);
}

// Provides a stable identity for the physical endpoint currently selected by
// the operating system. Backends expose this separately because some portable
// device lists represent the default route with a synthetic "default" entry.
public interface IDefaultAudioDeviceIdentityProvider
{
    string? GetDefaultDeviceIdentity(AudioDirection direction);
}

public interface IAudioCapture : IAsyncDisposable
{
    event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    PcmAudioFormat Format { get; }
    bool IsRunning { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IAudioPlayback : IAsyncDisposable
{
    PcmAudioFormat Format { get; }
    // Interleaved samples currently waiting for presentation, expressed in
    // this playback object's Format even when the native device runs at a
    // different sample rate.
    int? QueuedSamples => null;
    ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default);
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
    ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<int?>(null);
}

// Optional playback path for decoder-generated replacement audio. Consumers
// that present live audio may bound how much late concealment they admit while
// observers such as recorders can still retain the complete decoded timeline.
public interface IConcealmentAudioPlayback
{
    ValueTask WriteConcealmentAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default);
}

// Optional per-lane live-presentation control. Disabling presentation discards
// queued and future speaker-bound PCM without closing the decoder-facing
// playback object, allowing observers such as TAR writers to keep receiving the
// complete decoded stream.
public interface ILiveAudioPlaybackControl
{
    bool LivePlaybackEnabled { get; set; }
}

// Optional physical-output diagnostics. A backend begins continuity tracking
// when audio is written and commits a starvation gap only if playback later
// resumes. EndExpectedPlayback discards the normal empty tail after a call.
public interface IAudioPlaybackContinuityDiagnostics
{
    TimeSpan StarvedDuration { get; }
    void EndExpectedPlayback();
}

public interface IAudioGainControl
{
    double Gain { get; set; }
}

public interface IAudioBalanceControl
{
    double Balance { get; set; }
}

public interface IPttSource : IAsyncDisposable
{
    event EventHandler<bool>? StateChanged;
    bool IsPressed { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
