namespace DvmConsole.Audio;

internal enum VoiceEndpoint
{
    Capture,
    Playback
}

internal interface IVoiceProcessingPlaybackSession
{
    int QueuedSamples { get; }
    TimeSpan StarvedDuration { get; }
    TimeSpan PendingStarvedDuration { get; }
    long OutputCallbackCount { get; }
    TimeSpan OutputPresentationLatency { get; }
    void StartPlayback();
    void StopPlayback();
    int Write(short[] samples, int count);
    void EndExpectedPlayback();
}

internal static class VoiceProcessingSessionRegistry
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
        MacCoreAudioBackend.ValidateVoiceFormat(format);
        lock (Sync)
        {
            var key = new VoiceSessionKey(
                libraryPath,
                inputDeviceId,
                outputDeviceId);
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
        }
        // Native teardown can wait on CoreAudio. Never hold the registry-wide
        // lock while one physical device is stopping.
        session.Dispose();
    }

    internal readonly record struct VoiceSessionKey(
        string LibraryPath,
        ulong InputDeviceId,
        ulong OutputDeviceId);
}

internal sealed class VoiceProcessingSession : IDisposable, IVoiceProcessingPlaybackSession
{
    private readonly object sync = new();
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmAudioFormat format;
    private int captureEndpoints;
    private int playbackEndpoints;
    private int runningEndpoints;
    private bool disposed;

    public VoiceProcessingSession(
        VoiceProcessingSessionRegistry.VoiceSessionKey key,
        PcmAudioFormat format)
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
        if (stream.IsInvalid)
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
    public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
        api.GetVoiceProcessingStarvedSamples(stream) /
        (double)checked(format.SampleRate * format.Channels));
    public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
        api.GetVoiceProcessingPendingStarvedSamples(stream) /
        (double)checked(format.SampleRate * format.Channels));
    public long OutputCallbackCount => checked(
        (long)api.GetVoiceProcessingOutputCallbackCount(stream));
    public TimeSpan OutputPresentationLatency =>
        api.GetVoiceProcessingOutputPresentationLatency(stream);

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
    public int Write(short[] samples, int count)
        => api.WriteVoiceProcessing(stream, samples, count);
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
                stream.Dispose();
            }
            finally
            {
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
                    MacCoreAudioBackend.EnsureSuccess(result, "start Apple voice processing");
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
            {
                MacCoreAudioBackend.EnsureSuccess(
                    api.StopVoiceProcessing(stream),
                    "stop Apple voice processing");
            }
        }
    }
}
