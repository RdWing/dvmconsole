using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

/// <summary>
/// macOS CoreAudio backend. The native shim is loaded explicitly so the rest of
/// the application remains independent of CoreAudio and Windows audio APIs.
/// </summary>
public sealed class MacCoreAudioBackend : IAudioBackend
{
    private readonly NativeCoreAudioApi api;

    public MacCoreAudioBackend(string? libraryPath = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacCoreAudioBackend requires macOS.");

        api = NativeCoreAudioApi.Load(libraryPath);
    }

    public string Name => "macOS CoreAudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        int input = direction == AudioDirection.Input ? 1 : 0;
        int result = api.GetDeviceCount(input, out int count);
        EnsureSuccess(result, "enumerate audio devices");

        var devices = new List<AudioDeviceInfo>(count);
        for (int index = 0; index < count; index++)
        {
            byte[] name = new byte[256];
            result = api.GetDevice(input, index, out ulong deviceId, name, name.Length, out int isDefault);
            EnsureSuccess(result, "read audio device");
            string deviceName = System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = $"Audio device {deviceId}";
            devices.Add(new AudioDeviceInfo(deviceId.ToString(), deviceName, direction, isDefault != 0));
        }

        return devices;
    }

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        return new MacCoreAudioCapture(api, ParseDeviceId(device), format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        return new MacCoreAudioPlayback(api, ParseDeviceId(device), format);
    }

    public void Dispose() => api.Dispose();

    private static ulong ParseDeviceId(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Id is null || !ulong.TryParse(device.Id, out ulong deviceId))
            throw new ArgumentException("The CoreAudio device ID is invalid.", nameof(device));
        return deviceId;
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"Unable to {operation}; CoreAudio status {result}.");
    }

    private sealed class MacCoreAudioCapture : IAudioCapture
    {
        private readonly NativeCoreAudioApi api;
        private readonly IntPtr stream;
        private readonly PcmRateConverter? rateConverter;
        private CancellationTokenSource? pumpCancellation;
        private Task? pumpTask;
        private bool disposed;

        public MacCoreAudioCapture(NativeCoreAudioApi api, ulong deviceId, PcmAudioFormat format)
        {
            ValidateFormat(format);
            this.api = api;
            Format = format;
            stream = api.CreateStream(deviceId, input: 1, format.SampleRate, format.Channels, format.BitsPerSample);
            if (stream == IntPtr.Zero)
                throw new InvalidOperationException("CoreAudio could not create the capture stream.");
            int nativeSampleRate = api.GetSampleRate(stream);
            rateConverter = nativeSampleRate == format.SampleRate ? null : new PcmRateConverter(nativeSampleRate, format.SampleRate);
        }

        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; }
        public bool IsRunning => pumpCancellation is not null;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning)
                return ValueTask.CompletedTask;

            EnsureSuccess(api.StartStream(stream), "start CoreAudio capture");
            pumpCancellation = new CancellationTokenSource();
            pumpTask = PumpAsync(pumpCancellation.Token);
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource? cancellation = pumpCancellation;
            Task? task = pumpTask;
            pumpCancellation = null;
            pumpTask = null;
            cancellation?.Cancel();
            if (task is not null)
                await task.ConfigureAwait(false);
            cancellation?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSuccess(api.StopStream(stream), "stop CoreAudio capture");
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            await StopAsync().ConfigureAwait(false);
            api.DestroyStream(stream);
            disposed = true;
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            short[] buffer = new short[1600];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    int count = api.ReadStream(stream, buffer, buffer.Length);
                    if (count <= 0)
                        continue;

                    short[] samples = new short[count];
                    Array.Copy(buffer, samples, count);
                    if (rateConverter is not null)
                        samples = rateConverter.Convert(samples);
                    if (samples.Length == 0)
                        continue;
                    SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
    }

    private sealed class MacCoreAudioPlayback : IAudioPlayback
    {
        private readonly NativeCoreAudioApi api;
        private readonly IntPtr stream;
        private readonly PcmRateConverter? rateConverter;
        private bool disposed;

        public MacCoreAudioPlayback(NativeCoreAudioApi api, ulong deviceId, PcmAudioFormat format)
        {
            ValidateFormat(format);
            this.api = api;
            Format = format;
            stream = api.CreateStream(deviceId, input: 0, format.SampleRate, format.Channels, format.BitsPerSample);
            if (stream == IntPtr.Zero)
                throw new InvalidOperationException("CoreAudio could not create the playback stream.");
            int nativeSampleRate = api.GetSampleRate(stream);
            rateConverter = nativeSampleRate == format.SampleRate ? null : new PcmRateConverter(format.SampleRate, nativeSampleRate);
            EnsureSuccess(api.StartStream(stream), "start CoreAudio playback");
        }

        public PcmAudioFormat Format { get; }
        public int? QueuedSamples => checked((int)api.GetQueuedSamples(stream));

        public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            short[] buffer = rateConverter?.Convert(samples.Span) ?? samples.ToArray();
            int offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int written = api.WriteStream(stream, buffer.AsSpan(offset).ToArray(), buffer.Length - offset);
                EnsureSuccess(written < 0 ? written : 0, "write CoreAudio playback");
                offset += written;
                if (written == 0)
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                while ((QueuedSamples ?? 0) > 0)
                    await Task.Delay(5, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("macOS audio playback did not drain within five seconds.");
            }

            return initialSamples - (QueuedSamples ?? 0);
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                api.StopStream(stream);
                api.DestroyStream(stream);
                disposed = true;
            }
            return ValueTask.CompletedTask;
        }
    }

    private static void ValidateFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels != 1 || format.BitsPerSample != 16)
            throw new NotSupportedException("The macOS voice backend currently supports mono 16-bit PCM only.");
    }

    private sealed class NativeCoreAudioApi : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceCountDelegate(int input, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceDelegate(int input, int index, out ulong deviceId, byte[] name, int nameCapacity, out int isDefault);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateStreamDelegate(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamStatusDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSampleRateDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamReadDelegate(IntPtr stream, [Out] short[] samples, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWriteDelegate(IntPtr stream, short[] samples, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StreamQueuedSamplesDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyStreamDelegate(IntPtr stream);

        private readonly IntPtr handle;
        private readonly GetDeviceCountDelegate getDeviceCount;
        private readonly GetDeviceDelegate getDevice;
        private readonly CreateStreamDelegate createStream;
        private readonly StreamStatusDelegate startStream;
        private readonly StreamStatusDelegate stopStream;
        private readonly GetSampleRateDelegate getSampleRate;
        private readonly StreamReadDelegate readStream;
        private readonly StreamWriteDelegate writeStream;
        private readonly StreamQueuedSamplesDelegate queuedSamples;
        private readonly DestroyStreamDelegate destroyStream;

        private NativeCoreAudioApi(IntPtr handle)
        {
            this.handle = handle;
            getDeviceCount = Get<GetDeviceCountDelegate>("dvm_audio_get_device_count");
            getDevice = Get<GetDeviceDelegate>("dvm_audio_get_device");
            createStream = Get<CreateStreamDelegate>("dvm_audio_stream_create");
            startStream = Get<StreamStatusDelegate>("dvm_audio_stream_start");
            stopStream = Get<StreamStatusDelegate>("dvm_audio_stream_stop");
            getSampleRate = Get<GetSampleRateDelegate>("dvm_audio_stream_get_sample_rate");
            readStream = Get<StreamReadDelegate>("dvm_audio_stream_read");
            writeStream = Get<StreamWriteDelegate>("dvm_audio_stream_write");
            queuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_stream_queued_samples");
            destroyStream = Get<DestroyStreamDelegate>("dvm_audio_stream_destroy");
        }

        public static NativeCoreAudioApi Load(string? configuredPath)
        {
            string? path = configuredPath;
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppContext.BaseDirectory, "libdvmaudio.dylib");

            IntPtr handle = NativeLibrary.Load(Path.GetFullPath(path));
            try
            {
                return new NativeCoreAudioApi(handle);
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }
        }

        public int GetDeviceCount(int input, out int count) => getDeviceCount(input, out count);
        public int GetDevice(int input, int index, out ulong deviceId, byte[] name, int capacity, out int isDefault) => getDevice(input, index, out deviceId, name, capacity, out isDefault);
        public IntPtr CreateStream(ulong id, int input, int sampleRate, int channels, int bits) => createStream(id, input, sampleRate, channels, bits);
        public int StartStream(IntPtr stream) => startStream(stream);
        public int StopStream(IntPtr stream) => stopStream(stream);
        public int GetSampleRate(IntPtr stream) => getSampleRate(stream);
        public int ReadStream(IntPtr stream, short[] samples, int capacity) => readStream(stream, samples, capacity);
        public int WriteStream(IntPtr stream, short[] samples, int count) => writeStream(stream, samples, count);
        public uint GetQueuedSamples(IntPtr stream) => queuedSamples(stream);
        public void DestroyStream(IntPtr stream) => destroyStream(stream);
        public void Dispose() => NativeLibrary.Free(handle);

        private T Get<T>(string symbol) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol));
        }
    }
}
