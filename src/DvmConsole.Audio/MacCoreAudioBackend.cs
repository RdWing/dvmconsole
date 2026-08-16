using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

// macOS CoreAudio backend. The native shim is loaded explicitly so the rest of
// the application remains independent of CoreAudio and Windows audio APIs.
public sealed class MacCoreAudioBackend : IAudioBackend
{
    private readonly NativeCoreAudioApi api;
    private readonly AudioProcessingMode processingMode;
    private readonly string configuredInputDeviceId;
    private readonly string configuredOutputDeviceId;

    public MacCoreAudioBackend(
        string? libraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacCoreAudioBackend requires macOS.");

        this.processingMode = processingMode;
        configuredInputDeviceId = NormalizeConfiguredDeviceId(inputDeviceId);
        configuredOutputDeviceId = NormalizeConfiguredDeviceId(outputDeviceId);
        api = NativeCoreAudioApi.Load(libraryPath);
    }

    public string Name => "macOS CoreAudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        const int maximumAttempts = 8;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            int input = direction == AudioDirection.Input ? 1 : 0;
            int result = api.GetDeviceCount(input, out int count);
            EnsureSuccess(result, "enumerate audio devices");

            var devices = new List<AudioDeviceInfo>(count);
            bool changedDuringEnumeration = false;
            for (int index = 0; index < count; index++)
            {
                byte[] name = new byte[256];
                result = api.GetDevice(input, index, out ulong deviceId, name, name.Length, out int isDefault);
                if (result == -4)
                {
                    changedDuringEnumeration = true;
                    break;
                }
                EnsureSuccess(result, "read audio device");
                string deviceName = System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(deviceName))
                    deviceName = $"Audio device {deviceId}";
                devices.Add(new AudioDeviceInfo(deviceId.ToString(), deviceName, direction, isDefault != 0));
            }

            if (!changedDuringEnumeration)
                return devices;
            if (attempt + 1 < maximumAttempts)
                Thread.Sleep(40);
        }

        throw new InvalidOperationException("Unable to read the audio device list because CoreAudio is changing routes. Try again after the microphone mode finishes changing.");
    }

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
        {
            ulong inputDeviceId = ParseDeviceId(device);
            ulong outputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId);
            return new MacVoiceProcessingCapture(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    format,
                    VoiceEndpoint.Capture),
                format);
        }
        return new MacCoreAudioCapture(api, ParseDeviceId(device), format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong outputDeviceId = ParseDeviceId(device);
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing &&
            outputDeviceId == ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId))
        {
            ulong inputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Input, configuredInputDeviceId);
            return new MacVoiceProcessingPlayback(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    format,
                    VoiceEndpoint.Playback),
                format);
        }
        return new MacCoreAudioPlayback(api, outputDeviceId, format);
    }

    public void Dispose() => api.Dispose();

    private static ulong ParseDeviceId(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Id is null || !ulong.TryParse(device.Id, out ulong deviceId))
            throw new ArgumentException("The CoreAudio device ID is invalid.", nameof(device));
        return deviceId;
    }

    private ulong ResolveConfiguredDeviceId(AudioDirection direction, string configuredId)
    {
        IReadOnlyList<AudioDeviceInfo> devices = EnumerateDevices(direction);
        AudioDeviceInfo device = devices.FirstOrDefault(candidate =>
                !configuredId.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                candidate.Id.Equals(configuredId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(candidate => candidate.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException($"No {direction.ToString().ToLowerInvariant()} audio device is available.");
        return ParseDeviceId(device);
    }

    private static string NormalizeConfiguredDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId.Trim();

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

    private enum VoiceEndpoint
    {
        Capture,
        Playback
    }

    private sealed class MacVoiceProcessingCapture : IAudioCapture
    {
        private readonly VoiceProcessingSession session;
        private CancellationTokenSource? pumpCancellation;
        private Task? pumpTask;
        private bool disposed;

        public MacVoiceProcessingCapture(VoiceProcessingSession session, PcmAudioFormat format)
        {
            this.session = session;
            Format = format;
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

            session.StartCapture();
            pumpCancellation = new CancellationTokenSource();
            pumpTask = PumpAsync(pumpCancellation.Token);
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokenSource? cancellation = pumpCancellation;
            Task? task = pumpTask;
            if (cancellation is null)
                return;

            pumpCancellation = null;
            pumpTask = null;
            cancellation.Cancel();
            if (task is not null)
                await task.ConfigureAwait(false);
            cancellation.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            session.StopCapture();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            finally
            {
                VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Capture);
                disposed = true;
            }
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            short[] buffer = new short[Math.Max(1600, Format.SampleRate / 5)];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    int count = session.Read(buffer);
                    if (count <= 0)
                        continue;
                    short[] samples = new short[count];
                    Array.Copy(buffer, samples, count);
                    SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
    }

    private sealed class MacVoiceProcessingPlayback : IAudioPlayback
    {
        private readonly VoiceProcessingSession session;
        private bool disposed;

        public MacVoiceProcessingPlayback(VoiceProcessingSession session, PcmAudioFormat format)
        {
            this.session = session;
            Format = format;
            try
            {
                session.StartPlayback();
            }
            catch
            {
                VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Playback);
                throw;
            }
        }

        public PcmAudioFormat Format { get; }
        public int? QueuedSamples => session.QueuedSamples;

        public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            short[] buffer = samples.ToArray();
            int offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int written = session.Write(buffer.AsSpan(offset).ToArray());
                if (written < 0)
                    EnsureSuccess(written, "write Apple voice-processing playback");
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
                throw new TimeoutException("Apple voice-processing playback did not drain within five seconds.");
            }
            return initialSamples - (QueuedSamples ?? 0);
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                try
                {
                    session.StopPlayback();
                }
                finally
                {
                    VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Playback);
                    disposed = true;
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private static class VoiceProcessingSessionRegistry
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<VoiceSessionKey, VoiceProcessingSession> Sessions = [];

        public static VoiceProcessingSession Acquire(
            string libraryPath,
            ulong inputDeviceId,
            ulong outputDeviceId,
            PcmAudioFormat format,
            VoiceEndpoint endpoint)
        {
            ValidateFormat(format);
            lock (Sync)
            {
                var key = new VoiceSessionKey(libraryPath, inputDeviceId, outputDeviceId);
                if (!Sessions.TryGetValue(key, out VoiceProcessingSession? session))
                {
                    session = new VoiceProcessingSession(key, format);
                    Sessions.Add(key, session);
                }
                try
                {
                    session.AddEndpoint(format, endpoint);
                    return session;
                }
                catch
                {
                    if (session.HasNoEndpoints)
                    {
                        Sessions.Remove(key);
                        session.Dispose();
                    }
                    throw;
                }
            }
        }

        public static void Release(VoiceProcessingSession session, VoiceEndpoint endpoint)
        {
            lock (Sync)
            {
                if (!session.RemoveEndpoint(endpoint))
                    return;
                Sessions.Remove(session.Key);
                session.Dispose();
            }
        }

        public readonly record struct VoiceSessionKey(
            string LibraryPath,
            ulong InputDeviceId,
            ulong OutputDeviceId);
    }

    private sealed class VoiceProcessingSession : IDisposable
    {
        private readonly object sync = new();
        private readonly NativeCoreAudioApi api;
        private readonly IntPtr stream;
        private readonly PcmAudioFormat format;
        private int captureEndpoints;
        private int playbackEndpoints;
        private int runningEndpoints;
        private bool disposed;

        public VoiceProcessingSession(VoiceProcessingSessionRegistry.VoiceSessionKey key, PcmAudioFormat format)
        {
            Key = key;
            this.format = format;
            api = NativeCoreAudioApi.Load(key.LibraryPath);
            stream = api.CreateVoiceProcessingStream(
                key.InputDeviceId,
                key.OutputDeviceId,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample);
            if (stream == IntPtr.Zero)
            {
                api.Dispose();
                throw new InvalidOperationException(
                    "CoreAudio could not create the Apple Voice Processing I/O stream. " +
                    "Confirm that the selected input/output pair supports full-duplex voice audio.");
            }
        }

        public VoiceProcessingSessionRegistry.VoiceSessionKey Key { get; }
        public bool HasNoEndpoints => captureEndpoints == 0 && playbackEndpoints == 0;
        public int QueuedSamples => checked((int)api.GetVoiceProcessingQueuedSamples(stream));

        public void AddEndpoint(PcmAudioFormat requestedFormat, VoiceEndpoint endpoint)
        {
            if (requestedFormat != format)
                throw new InvalidOperationException("Apple voice-processing capture and playback must use the same PCM format.");
            if (endpoint == VoiceEndpoint.Capture)
            {
                if (captureEndpoints != 0)
                    throw new InvalidOperationException("Only one Apple voice-processing microphone endpoint can be open.");
                captureEndpoints++;
            }
            else
            {
                if (playbackEndpoints != 0)
                    throw new InvalidOperationException(
                        "Only the final mixed radio output can use Apple voice processing. " +
                        "Additional physical output routes use the normal CoreAudio path.");
                playbackEndpoints++;
            }
        }

        public bool RemoveEndpoint(VoiceEndpoint endpoint)
        {
            if (endpoint == VoiceEndpoint.Capture)
                captureEndpoints--;
            else
                playbackEndpoints--;
            return captureEndpoints == 0 && playbackEndpoints == 0;
        }

        public void StartCapture() => StartEndpoint();
        public void StopCapture() => StopEndpoint();
        public void StartPlayback() => StartEndpoint();
        public void StopPlayback() => StopEndpoint();
        public int Read(short[] samples) => api.ReadVoiceProcessing(stream, samples, samples.Length);
        public int Write(short[] samples) => api.WriteVoiceProcessing(stream, samples, samples.Length);

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                if (runningEndpoints > 0)
                    api.StopVoiceProcessing(stream);
                api.DestroyVoiceProcessingStream(stream);
                api.Dispose();
                runningEndpoints = 0;
                disposed = true;
            }
        }

        private void StartEndpoint()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (runningEndpoints++ == 0)
                {
                    int result = api.StartVoiceProcessing(stream);
                    if (result != 0)
                    {
                        runningEndpoints--;
                        EnsureSuccess(result, "start Apple voice processing");
                    }
                }
            }
        }

        private void StopEndpoint()
        {
            lock (sync)
            {
                if (runningEndpoints <= 0)
                    return;
                if (--runningEndpoints == 0)
                    EnsureSuccess(api.StopVoiceProcessing(stream), "stop Apple voice processing");
            }
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateVoiceProcessingStreamDelegate(ulong inputDeviceId, ulong outputDeviceId, int sampleRate, int channels, int bitsPerSample);

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
        private readonly CreateVoiceProcessingStreamDelegate createVoiceProcessingStream;
        private readonly StreamStatusDelegate startVoiceProcessing;
        private readonly StreamStatusDelegate stopVoiceProcessing;
        private readonly StreamReadDelegate readVoiceProcessing;
        private readonly StreamWriteDelegate writeVoiceProcessing;
        private readonly StreamQueuedSamplesDelegate voiceProcessingQueuedSamples;
        private readonly DestroyStreamDelegate destroyVoiceProcessingStream;

        private NativeCoreAudioApi(IntPtr handle, string libraryPath)
        {
            this.handle = handle;
            LibraryPath = libraryPath;
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
            createVoiceProcessingStream = Get<CreateVoiceProcessingStreamDelegate>("dvm_audio_voice_processing_create");
            startVoiceProcessing = Get<StreamStatusDelegate>("dvm_audio_voice_processing_start");
            stopVoiceProcessing = Get<StreamStatusDelegate>("dvm_audio_voice_processing_stop");
            readVoiceProcessing = Get<StreamReadDelegate>("dvm_audio_voice_processing_read");
            writeVoiceProcessing = Get<StreamWriteDelegate>("dvm_audio_voice_processing_write");
            voiceProcessingQueuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_voice_processing_queued_samples");
            destroyVoiceProcessingStream = Get<DestroyStreamDelegate>("dvm_audio_voice_processing_destroy");
        }

        public string LibraryPath { get; }

        public static NativeCoreAudioApi Load(string? configuredPath)
        {
            string? path = configuredPath;
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppContext.BaseDirectory, "libdvmaudio.dylib");

            string fullPath = Path.GetFullPath(path);
            IntPtr handle = NativeLibrary.Load(fullPath);
            try
            {
                return new NativeCoreAudioApi(handle, fullPath);
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
        public IntPtr CreateVoiceProcessingStream(ulong inputDeviceId, ulong outputDeviceId, int sampleRate, int channels, int bits) => createVoiceProcessingStream(inputDeviceId, outputDeviceId, sampleRate, channels, bits);
        public int StartVoiceProcessing(IntPtr stream) => startVoiceProcessing(stream);
        public int StopVoiceProcessing(IntPtr stream) => stopVoiceProcessing(stream);
        public int ReadVoiceProcessing(IntPtr stream, short[] samples, int capacity) => readVoiceProcessing(stream, samples, capacity);
        public int WriteVoiceProcessing(IntPtr stream, short[] samples, int count) => writeVoiceProcessing(stream, samples, count);
        public uint GetVoiceProcessingQueuedSamples(IntPtr stream) => voiceProcessingQueuedSamples(stream);
        public void DestroyVoiceProcessingStream(IntPtr stream) => destroyVoiceProcessingStream(stream);
        public void Dispose() => NativeLibrary.Free(handle);

        private T Get<T>(string symbol) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol));
        }
    }
}
