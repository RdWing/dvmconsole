using NAudio.Wave;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

/// <summary>
/// Windows audio backend using NAudio's WinMM wave input/output adapters.
/// The managed contract remains independent of NAudio and the implementation
/// is only constructible on Windows.
/// </summary>
public sealed class WindowsAudioBackend : IAudioBackend
{
    private const string DefaultDeviceId = "default";

    public WindowsAudioBackend()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsAudioBackend requires Windows.");
    }

    public string Name => "Windows NAudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        var devices = new List<AudioDeviceInfo>
        {
            new(
                DefaultDeviceId,
                direction == AudioDirection.Input ? "Windows default input" : "Windows default output",
                direction,
                true)
        };

        if (direction == AudioDirection.Input)
        {
            for (int index = 0; index < WaveInEvent.DeviceCount; index++)
                devices.Add(new AudioDeviceInfo(index.ToString(), WaveInEvent.GetCapabilities(index).ProductName, direction, false));
        }
        else
        {
            for (int index = 0; index < GetWaveOutDeviceCount(); index++)
                devices.Add(new AudioDeviceInfo(index.ToString(), GetWaveOutDeviceName(index), direction, false));
        }

        return devices;
    }

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateFormat(format);
        return new WindowsAudioCapture(ParseDeviceNumber(device), format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateFormat(format);
        return new WindowsAudioPlayback(ParseDeviceNumber(device), format);
    }

    public void Dispose()
    {
    }

    private static int ParseDeviceNumber(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.Equals(device.Id, DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            return -1;
        if (device.Id is null || !int.TryParse(device.Id, out int deviceNumber) || deviceNumber < 0)
            throw new ArgumentException("The Windows audio device ID is invalid.", nameof(device));
        return deviceNumber;
    }

    private static void ValidateFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels != 1 || format.BitsPerSample != 16)
            throw new NotSupportedException("The Windows voice backend currently supports mono 16-bit PCM only.");
    }

    private static int GetWaveOutDeviceCount() => checked((int)waveOutGetNumDevs());

    private static string GetWaveOutDeviceName(int deviceNumber)
    {
        int result = waveOutGetDevCaps(deviceNumber, out NativeWaveOutCapabilities capabilities, Marshal.SizeOf<NativeWaveOutCapabilities>());
        return result == 0 && !string.IsNullOrWhiteSpace(capabilities.ProductName)
            ? capabilities.ProductName.Trim()
            : $"Windows output {deviceNumber}";
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int waveOutGetDevCaps(int deviceNumber, out NativeWaveOutCapabilities capabilities, int capabilitiesSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWaveOutCapabilities
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    private sealed class WindowsAudioCapture : IAudioCapture
    {
        private readonly WaveInEvent input;
        private bool running;
        private bool disposed;
        private Exception? captureFailure;

        public WindowsAudioCapture(int deviceNumber, PcmAudioFormat format)
        {
            Format = format;
            input = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels),
                BufferMilliseconds = 20
            };
            input.DataAvailable += HandleDataAvailable;
            input.RecordingStopped += HandleRecordingStopped;
        }

        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; }
        public bool IsRunning => running;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfCaptureFailed();
            if (!running)
            {
                input.StartRecording();
                running = true;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (running)
            {
                input.StopRecording();
                running = false;
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            await StopAsync().ConfigureAwait(false);
            input.DataAvailable -= HandleDataAvailable;
            input.RecordingStopped -= HandleRecordingStopped;
            input.Dispose();
            disposed = true;
        }

        private void HandleDataAvailable(object? sender, WaveInEventArgs args)
        {
            int sampleCount = args.BytesRecorded / sizeof(short);
            if (sampleCount == 0)
                return;

            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(args.Buffer, 0, samples, 0, sampleCount * sizeof(short));
            SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
        }

        private void HandleRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (!disposed && args.Exception is not null)
                Interlocked.CompareExchange(ref captureFailure, args.Exception, null);
        }

        private void ThrowIfCaptureFailed()
        {
            Exception? failure = Volatile.Read(ref captureFailure);
            if (failure is not null)
                throw new IOException("Windows audio capture stopped unexpectedly.", failure);
        }
    }

    private sealed class WindowsAudioPlayback : IAudioPlayback
    {
        private readonly WaveOutEvent output;
        private readonly BufferedWaveProvider buffer;
        private bool disposed;
        private Exception? playbackFailure;

        public WindowsAudioPlayback(int deviceNumber, PcmAudioFormat format)
        {
            Format = format;
            buffer = new BufferedWaveProvider(new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels))
            {
                DiscardOnBufferOverflow = false
            };
            output = new WaveOutEvent { DeviceNumber = deviceNumber };
            output.Init(buffer);
            output.PlaybackStopped += HandlePlaybackStopped;
            output.Play();
        }

        public PcmAudioFormat Format { get; }
        public int? QueuedSamples => buffer.BufferedBytes / sizeof(short);

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfPlaybackFailed();
            if (!samples.IsEmpty)
            {
                byte[] bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
                buffer.AddSamples(bytes, 0, bytes.Length);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfPlaybackFailed();
            buffer.ClearBuffer();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            int initialSamples = QueuedSamples ?? 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (buffer.BufferedBytes > 0)
                {
                    ThrowIfPlaybackFailed();
                    await Task.Delay(5, timeout.Token).ConfigureAwait(false);
                }
                ThrowIfPlaybackFailed();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Windows audio playback did not drain within five seconds.");
            }

            return initialSamples - (QueuedSamples ?? 0);
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                output.PlaybackStopped -= HandlePlaybackStopped;
                output.Stop();
                output.Dispose();
                disposed = true;
            }
            return ValueTask.CompletedTask;
        }

        private void HandlePlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (!disposed && args.Exception is not null)
                Interlocked.CompareExchange(ref playbackFailure, args.Exception, null);
        }

        private void ThrowIfPlaybackFailed()
        {
            Exception? failure = Volatile.Read(ref playbackFailure);
            if (failure is not null)
                throw new IOException("Windows audio playback stopped unexpectedly.", failure);
        }
    }
}
