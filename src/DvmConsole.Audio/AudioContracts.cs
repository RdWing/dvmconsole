namespace DvmConsole.Audio;

public enum AudioDirection
{
    Input,
    Output
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDirection Direction,
    bool IsDefault);

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
    ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default);
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

public interface IPttSource : IAsyncDisposable
{
    event EventHandler<bool>? StateChanged;
    bool IsPressed { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
