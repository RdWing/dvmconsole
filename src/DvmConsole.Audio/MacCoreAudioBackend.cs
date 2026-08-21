using System.Buffers;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

// macOS CoreAudio backend. The native shim is loaded explicitly so the rest of
// the application remains independent of CoreAudio and Windows audio APIs.
public sealed class MacCoreAudioBackend :
    IAudioBackend,
    IDefaultAudioDeviceIdentityProvider,
    IHighQualityBluetoothAudioStatus
{
    private readonly NativeCoreAudioApi api;
    private readonly AudioProcessingMode processingMode;
    private readonly string configuredInputDeviceId;
    private readonly string configuredOutputDeviceId;
    private readonly bool highQualityBluetoothAudio;

    public MacCoreAudioBackend(
        string? libraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null,
        bool highQualityBluetoothAudio = false)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacCoreAudioBackend requires macOS.");

        this.processingMode = processingMode;
        configuredInputDeviceId = NormalizeConfiguredDeviceId(inputDeviceId);
        configuredOutputDeviceId = NormalizeConfiguredDeviceId(outputDeviceId);
        this.highQualityBluetoothAudio = highQualityBluetoothAudio;
        api = NativeCoreAudioApi.Load(libraryPath);
    }

    public string Name => "macOS CoreAudio";
    public HighQualityBluetoothAudioStatus HighQualityBluetoothStatus
        => (HighQualityBluetoothAudioStatus)api.GetHighQualityBluetoothStatus();

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
                int bluetooth = api.IsBluetoothDevice(deviceId);
                devices.Add(new AudioDeviceInfo(
                    deviceId.ToString(),
                    deviceName,
                    direction,
                    isDefault != 0,
                    bluetooth < 0 ? null : bluetooth != 0));
            }

            if (!changedDuringEnumeration)
                return devices;
            if (attempt + 1 < maximumAttempts)
                Thread.Sleep(40);
        }

        throw new InvalidOperationException("Unable to read the audio device list because CoreAudio is changing routes. Try again after the microphone mode finishes changing.");
    }

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
        => EnumerateDevices(direction).FirstOrDefault(device => device.IsDefault)?.Id;

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong inputDeviceId = ParseDeviceId(device);
        ulong outputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId);
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
        {
            EnsureVoiceProcessingPairSupported(inputDeviceId, outputDeviceId);
            return new MacVoiceProcessingCapture(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    highQualityBluetoothAudio,
                    format,
                    VoiceEndpoint.Capture),
                format);
        }
        return new MacCoreAudioCapture(
            api,
            inputDeviceId,
            outputDeviceId,
            highQualityBluetoothAudio,
            format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong outputDeviceId = ParseDeviceId(device);
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing &&
            outputDeviceId == ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId))
        {
            ulong inputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Input, configuredInputDeviceId);
            EnsureVoiceProcessingPairSupported(inputDeviceId, outputDeviceId);
            return new MacVoiceProcessingPlayback(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    highQualityBluetoothAudio,
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

    private void EnsureVoiceProcessingPairSupported(ulong inputDeviceId, ulong outputDeviceId)
    {
        if (inputDeviceId == outputDeviceId)
            return;

        bool inputIsDefault = EnumerateDevices(AudioDirection.Input)
            .Any(device => device.IsDefault && ParseDeviceId(device) == inputDeviceId);
        bool outputIsDefault = EnumerateDevices(AudioDirection.Output)
            .Any(device => device.IsDefault && ParseDeviceId(device) == outputDeviceId);
        if (inputIsDefault && outputIsDefault)
            return;

        throw new NotSupportedException(
            "Apple voice processing on macOS requires the system-default input/output pair " +
            "or one duplex device that provides both input and output. Choose a compatible paired route " +
            "or use DVM Console processing for separate devices.");
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
        private readonly bool highQualitySessionAcquired;
        private CancellationTokenSource? pumpCancellation;
        private Task? pumpTask;
        private bool disposed;

        public MacCoreAudioCapture(
            NativeCoreAudioApi api,
            ulong deviceId,
            ulong outputDeviceId,
            bool highQualityBluetoothAudio,
            PcmAudioFormat format)
        {
            ValidateVoiceFormat(format);
            this.api = api;
            Format = format;
            highQualitySessionAcquired = highQualityBluetoothAudio &&
                api.AcquireHighQualityBluetooth(deviceId, outputDeviceId) != 0;
            stream = api.CreateStream(deviceId, input: 1, format.SampleRate, format.Channels, format.BitsPerSample);
            if (stream == IntPtr.Zero)
            {
                if (highQualitySessionAcquired)
                    api.ReleaseHighQualityBluetooth();
                throw new InvalidOperationException("CoreAudio could not create the capture stream.");
            }
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
            try
            {
                api.DestroyStream(stream);
            }
            finally
            {
                if (highQualitySessionAcquired)
                    api.ReleaseHighQualityBluetooth();
                disposed = true;
            }
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

    private sealed class MacCoreAudioPlayback :
        IAudioPlayback,
        IAudioPlaybackContinuityDiagnostics,
        IAudioPlaybackCallbackDiagnostics
    {
        private readonly NativeCoreAudioApi api;
        private readonly IntPtr stream;
        private readonly PcmRateConverter? rateConverter;
        private readonly int nativeSampleRate;
        private bool disposed;

        public MacCoreAudioPlayback(NativeCoreAudioApi api, ulong deviceId, PcmAudioFormat format)
        {
            ValidatePlaybackFormat(format);
            this.api = api;
            Format = format;
            stream = api.CreateStream(deviceId, input: 0, format.SampleRate, format.Channels, format.BitsPerSample);
            if (stream == IntPtr.Zero)
                throw new InvalidOperationException("CoreAudio could not create the playback stream.");
            nativeSampleRate = api.GetSampleRate(stream);
            rateConverter = nativeSampleRate == format.SampleRate
                ? null
                : new PcmRateConverter(format.SampleRate, nativeSampleRate, format.Channels);
            EnsureSuccess(api.StartStream(stream), "start CoreAudio playback");
        }

        public PcmAudioFormat Format { get; }
        public int? QueuedSamples => ConvertQueueDepthToRequestedRate(
            api.GetQueuedSamples(stream),
            nativeSampleRate,
            Format.SampleRate);
        public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
            api.GetStarvedSamples(stream) /
            (double)checked(nativeSampleRate * Format.Channels));
        public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
            api.GetPendingStarvedSamples(stream) /
            (double)checked(nativeSampleRate * Format.Channels));
        public long OutputCallbackCount => checked((long)api.GetOutputCallbackCount(stream));

        public void EndExpectedPlayback()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            api.EndPlaybackContinuity(stream);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (rateConverter is null)
            {
                await WriteUnconvertedAsync(samples, cancellationToken).ConfigureAwait(false);
                return;
            }

            int maximumOutputSamples = rateConverter.GetMaximumOutputSampleCount(samples.Length);
            if (maximumOutputSamples == 0)
            {
                rateConverter.Convert(samples.Span, Span<short>.Empty);
                return;
            }

            short[] buffer = ArrayPool<short>.Shared.Rent(maximumOutputSamples);
            try
            {
                int convertedSamples = rateConverter.Convert(
                    samples.Span,
                    buffer.AsSpan(0, maximumOutputSamples));
                await WriteConvertedAsync(
                    buffer,
                    convertedSamples,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<short>.Shared.Return(buffer);
            }
        }

        private async ValueTask WriteUnconvertedAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken)
        {
            short[] buffer = GetZeroOffsetArrayOrCopy(samples);
            int offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                short[] writeBuffer = offset == 0
                    ? buffer
                    : buffer.AsSpan(offset).ToArray();
                int written = api.WriteStream(stream, writeBuffer, buffer.Length - offset);
                EnsureSuccess(written < 0 ? written : 0, "write CoreAudio playback");
                offset += written;
                if (written == 0)
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask WriteConvertedAsync(
            short[] buffer,
            int sampleCount,
            CancellationToken cancellationToken)
        {
            int remaining = sampleCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int written = api.WriteStream(stream, buffer, remaining);
                EnsureSuccess(written < 0 ? written : 0, "write CoreAudio playback");
                if (written > 0)
                {
                    remaining -= written;
                    if (remaining > 0)
                        Array.Copy(buffer, written, buffer, 0, remaining);
                }
                else
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static short[] GetZeroOffsetArrayOrCopy(ReadOnlyMemory<short> samples)
        {
            if (MemoryMarshal.TryGetArray(samples, out ArraySegment<short> segment) &&
                segment.Array is short[] array &&
                segment.Offset == 0 &&
                segment.Count == array.Length)
            {
                return array;
            }
            return samples.ToArray();
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

    private sealed class MacVoiceProcessingPlayback :
        IAudioPlayback,
        IAudioPlaybackContinuityDiagnostics,
        IAudioPlaybackCallbackDiagnostics
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
        public TimeSpan StarvedDuration => session.StarvedDuration;
        public TimeSpan PendingStarvedDuration => session.PendingStarvedDuration;
        public long OutputCallbackCount => session.OutputCallbackCount;

        public void EndExpectedPlayback()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session.EndExpectedPlayback();
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            short[] buffer;
            if (MemoryMarshal.TryGetArray(samples, out ArraySegment<short> segment) &&
                segment.Array is short[] array &&
                segment.Offset == 0 &&
                segment.Count == array.Length)
            {
                buffer = array;
            }
            else
            {
                buffer = samples.ToArray();
            }
            int offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int written = session.Write(
                    offset == 0 ? buffer : buffer.AsSpan(offset).ToArray());
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
            bool highQualityBluetoothAudio,
            PcmAudioFormat format,
            VoiceEndpoint endpoint)
        {
            ValidateVoiceFormat(format);
            lock (Sync)
            {
                var key = new VoiceSessionKey(
                    libraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    highQualityBluetoothAudio);
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
            ulong OutputDeviceId,
            bool HighQualityBluetoothAudio);
    }

    private sealed class VoiceProcessingSession : IDisposable
    {
        private readonly object sync = new();
        private readonly NativeCoreAudioApi api;
        private readonly IntPtr stream;
        private readonly PcmAudioFormat format;
        private readonly bool highQualitySessionAcquired;
        private int captureEndpoints;
        private int playbackEndpoints;
        private int runningEndpoints;
        private bool disposed;

        public VoiceProcessingSession(VoiceProcessingSessionRegistry.VoiceSessionKey key, PcmAudioFormat format)
        {
            Key = key;
            this.format = format;
            api = NativeCoreAudioApi.Load(key.LibraryPath);
            highQualitySessionAcquired = key.HighQualityBluetoothAudio &&
                api.AcquireHighQualityBluetooth(key.InputDeviceId, key.OutputDeviceId) != 0;
            stream = api.CreateVoiceProcessingStream(
                key.InputDeviceId,
                key.OutputDeviceId,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample);
            if (stream == IntPtr.Zero)
            {
                if (highQualitySessionAcquired)
                    api.ReleaseHighQualityBluetooth();
                api.Dispose();
                throw new InvalidOperationException(
                    "CoreAudio could not create the Apple Voice Processing I/O stream. " +
                    "Confirm that the selected input/output pair supports full-duplex voice audio.");
            }
        }

        public VoiceProcessingSessionRegistry.VoiceSessionKey Key { get; }
        public bool HasNoEndpoints => captureEndpoints == 0 && playbackEndpoints == 0;
        public int QueuedSamples => checked((int)api.GetVoiceProcessingQueuedSamples(stream));
        public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
            api.GetVoiceProcessingStarvedSamples(stream) /
            (double)checked(format.SampleRate * format.Channels));
        public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
            api.GetVoiceProcessingPendingStarvedSamples(stream) /
            (double)checked(format.SampleRate * format.Channels));
        public long OutputCallbackCount => checked(
            (long)api.GetVoiceProcessingOutputCallbackCount(stream));

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
        public void EndExpectedPlayback() => api.EndVoiceProcessingPlaybackContinuity(stream);

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                if (runningEndpoints > 0)
                    api.StopVoiceProcessing(stream);
                try
                {
                    api.DestroyVoiceProcessingStream(stream);
                }
                finally
                {
                    if (highQualitySessionAcquired)
                        api.ReleaseHighQualityBluetooth();
                    api.Dispose();
                }
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

    private static void ValidateVoiceFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels != 1 || format.BitsPerSample != 16)
            throw new NotSupportedException("The macOS voice backend currently supports mono 16-bit PCM only.");
    }

    private static void ValidatePlaybackFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels is not (1 or 2) || format.BitsPerSample != 16)
            throw new NotSupportedException("The macOS audio backend supports mono or stereo 16-bit playback.");
    }

    internal static int ConvertQueueDepthToRequestedRate(
        uint nativeSamples,
        int nativeSampleRate,
        int requestedSampleRate)
    {
        if (nativeSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativeSampleRate));
        if (requestedSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSampleRate));

        long scaled = checked((long)nativeSamples * requestedSampleRate);
        return checked((int)((scaled + nativeSampleRate - 1) / nativeSampleRate));
    }

    private sealed class NativeCoreAudioApi : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceCountDelegate(int input, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceDelegate(int input, int index, out ulong deviceId, byte[] name, int nameCapacity, out int isDefault);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsBluetoothDeviceDelegate(ulong deviceId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateStreamDelegate(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamStatusDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSampleRateDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamReadDelegate(IntPtr stream, [Out] short[] samples, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWriteDelegate(IntPtr stream, short[] samples, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StreamQueuedSamplesDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong StreamStarvedSamplesDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EndPlaybackContinuityDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyStreamDelegate(IntPtr stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateVoiceProcessingStreamDelegate(ulong inputDeviceId, ulong outputDeviceId, int sampleRate, int channels, int bitsPerSample);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AcquireHighQualityBluetoothDelegate(ulong inputDeviceId, ulong outputDeviceId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseHighQualityBluetoothDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHighQualityBluetoothStatusDelegate();

        private readonly IntPtr handle;
        private readonly GetDeviceCountDelegate getDeviceCount;
        private readonly GetDeviceDelegate getDevice;
        private readonly IsBluetoothDeviceDelegate? isBluetoothDevice;
        private readonly CreateStreamDelegate createStream;
        private readonly StreamStatusDelegate startStream;
        private readonly StreamStatusDelegate stopStream;
        private readonly GetSampleRateDelegate getSampleRate;
        private readonly StreamReadDelegate readStream;
        private readonly StreamWriteDelegate writeStream;
        private readonly StreamQueuedSamplesDelegate queuedSamples;
        private readonly StreamStarvedSamplesDelegate starvedSamples;
        private readonly StreamStarvedSamplesDelegate pendingStarvedSamples;
        private readonly StreamStarvedSamplesDelegate outputCallbackCount;
        private readonly EndPlaybackContinuityDelegate endPlaybackContinuity;
        private readonly DestroyStreamDelegate destroyStream;
        private readonly CreateVoiceProcessingStreamDelegate createVoiceProcessingStream;
        private readonly StreamStatusDelegate startVoiceProcessing;
        private readonly StreamStatusDelegate stopVoiceProcessing;
        private readonly StreamReadDelegate readVoiceProcessing;
        private readonly StreamWriteDelegate writeVoiceProcessing;
        private readonly StreamQueuedSamplesDelegate voiceProcessingQueuedSamples;
        private readonly StreamStarvedSamplesDelegate voiceProcessingStarvedSamples;
        private readonly StreamStarvedSamplesDelegate voiceProcessingPendingStarvedSamples;
        private readonly StreamStarvedSamplesDelegate voiceProcessingOutputCallbackCount;
        private readonly EndPlaybackContinuityDelegate endVoiceProcessingPlaybackContinuity;
        private readonly DestroyStreamDelegate destroyVoiceProcessingStream;
        private readonly AcquireHighQualityBluetoothDelegate acquireHighQualityBluetooth;
        private readonly ReleaseHighQualityBluetoothDelegate releaseHighQualityBluetooth;
        private readonly GetHighQualityBluetoothStatusDelegate getHighQualityBluetoothStatus;

        private NativeCoreAudioApi(IntPtr handle, string libraryPath)
        {
            this.handle = handle;
            LibraryPath = libraryPath;
            getDeviceCount = Get<GetDeviceCountDelegate>("dvm_audio_get_device_count");
            getDevice = Get<GetDeviceDelegate>("dvm_audio_get_device");
            isBluetoothDevice = TryGet<IsBluetoothDeviceDelegate>("dvm_audio_device_is_bluetooth");
            createStream = Get<CreateStreamDelegate>("dvm_audio_stream_create");
            startStream = Get<StreamStatusDelegate>("dvm_audio_stream_start");
            stopStream = Get<StreamStatusDelegate>("dvm_audio_stream_stop");
            getSampleRate = Get<GetSampleRateDelegate>("dvm_audio_stream_get_sample_rate");
            readStream = Get<StreamReadDelegate>("dvm_audio_stream_read");
            writeStream = Get<StreamWriteDelegate>("dvm_audio_stream_write");
            queuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_stream_queued_samples");
            starvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_starved_samples");
            pendingStarvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_pending_starved_samples");
            outputCallbackCount = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_output_callback_count");
            endPlaybackContinuity = Get<EndPlaybackContinuityDelegate>("dvm_audio_stream_end_playback_continuity");
            destroyStream = Get<DestroyStreamDelegate>("dvm_audio_stream_destroy");
            createVoiceProcessingStream = Get<CreateVoiceProcessingStreamDelegate>("dvm_audio_voice_processing_create");
            startVoiceProcessing = Get<StreamStatusDelegate>("dvm_audio_voice_processing_start");
            stopVoiceProcessing = Get<StreamStatusDelegate>("dvm_audio_voice_processing_stop");
            readVoiceProcessing = Get<StreamReadDelegate>("dvm_audio_voice_processing_read");
            writeVoiceProcessing = Get<StreamWriteDelegate>("dvm_audio_voice_processing_write");
            voiceProcessingQueuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_voice_processing_queued_samples");
            voiceProcessingStarvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_voice_processing_starved_samples");
            voiceProcessingPendingStarvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_voice_processing_pending_starved_samples");
            voiceProcessingOutputCallbackCount = Get<StreamStarvedSamplesDelegate>("dvm_audio_voice_processing_output_callback_count");
            endVoiceProcessingPlaybackContinuity = Get<EndPlaybackContinuityDelegate>("dvm_audio_voice_processing_end_playback_continuity");
            destroyVoiceProcessingStream = Get<DestroyStreamDelegate>("dvm_audio_voice_processing_destroy");
            acquireHighQualityBluetooth = Get<AcquireHighQualityBluetoothDelegate>("dvm_audio_high_quality_bluetooth_acquire");
            releaseHighQualityBluetooth = Get<ReleaseHighQualityBluetoothDelegate>("dvm_audio_high_quality_bluetooth_release");
            getHighQualityBluetoothStatus = Get<GetHighQualityBluetoothStatusDelegate>("dvm_audio_high_quality_bluetooth_status");
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
        public int IsBluetoothDevice(ulong deviceId) => isBluetoothDevice?.Invoke(deviceId) ?? -1;
        public IntPtr CreateStream(ulong id, int input, int sampleRate, int channels, int bits) => createStream(id, input, sampleRate, channels, bits);
        public int StartStream(IntPtr stream) => startStream(stream);
        public int StopStream(IntPtr stream) => stopStream(stream);
        public int GetSampleRate(IntPtr stream) => getSampleRate(stream);
        public int ReadStream(IntPtr stream, short[] samples, int capacity) => readStream(stream, samples, capacity);
        public int WriteStream(IntPtr stream, short[] samples, int count) => writeStream(stream, samples, count);
        public uint GetQueuedSamples(IntPtr stream) => queuedSamples(stream);
        public ulong GetStarvedSamples(IntPtr stream) => starvedSamples(stream);
        public ulong GetPendingStarvedSamples(IntPtr stream) => pendingStarvedSamples(stream);
        public ulong GetOutputCallbackCount(IntPtr stream) => outputCallbackCount(stream);
        public void EndPlaybackContinuity(IntPtr stream) => endPlaybackContinuity(stream);
        public void DestroyStream(IntPtr stream) => destroyStream(stream);
        public IntPtr CreateVoiceProcessingStream(ulong inputDeviceId, ulong outputDeviceId, int sampleRate, int channels, int bits) => createVoiceProcessingStream(inputDeviceId, outputDeviceId, sampleRate, channels, bits);
        public int StartVoiceProcessing(IntPtr stream) => startVoiceProcessing(stream);
        public int StopVoiceProcessing(IntPtr stream) => stopVoiceProcessing(stream);
        public int ReadVoiceProcessing(IntPtr stream, short[] samples, int capacity) => readVoiceProcessing(stream, samples, capacity);
        public int WriteVoiceProcessing(IntPtr stream, short[] samples, int count) => writeVoiceProcessing(stream, samples, count);
        public uint GetVoiceProcessingQueuedSamples(IntPtr stream) => voiceProcessingQueuedSamples(stream);
        public ulong GetVoiceProcessingStarvedSamples(IntPtr stream) => voiceProcessingStarvedSamples(stream);
        public ulong GetVoiceProcessingPendingStarvedSamples(IntPtr stream) => voiceProcessingPendingStarvedSamples(stream);
        public ulong GetVoiceProcessingOutputCallbackCount(IntPtr stream) => voiceProcessingOutputCallbackCount(stream);
        public void EndVoiceProcessingPlaybackContinuity(IntPtr stream) => endVoiceProcessingPlaybackContinuity(stream);
        public void DestroyVoiceProcessingStream(IntPtr stream) => destroyVoiceProcessingStream(stream);
        public int AcquireHighQualityBluetooth(ulong inputDeviceId, ulong outputDeviceId) => acquireHighQualityBluetooth(inputDeviceId, outputDeviceId);
        public void ReleaseHighQualityBluetooth() => releaseHighQualityBluetooth();
        public int GetHighQualityBluetoothStatus() => getHighQualityBluetoothStatus();
        public void Dispose() => NativeLibrary.Free(handle);

        private T Get<T>(string symbol) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol));
        }

        private T? TryGet<T>(string symbol) where T : Delegate
        {
            return NativeLibrary.TryGetExport(handle, symbol, out IntPtr address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;
        }
    }
}
