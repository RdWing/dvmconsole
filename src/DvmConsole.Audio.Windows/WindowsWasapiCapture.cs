using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DvmConsole.Audio;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWasapiCapture : IAudioCapture
{
    private const int BufferLengthMilliseconds = 20;
    private const string MmcssTaskName = "Audio";

    private readonly IDisposable? device;
    private readonly IWindowsWasapiRecorder recorder;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateSync = new();
    private TaskCompletionSource? stopped;
    private Exception? captureFailure;
    private bool running;
    private bool disposing;
    private bool disposed;

    public WindowsWasapiCapture(
        MMDevice device,
        PcmAudioFormat format,
        bool useCommunicationsMode)
        : this(
            BuildRecorder(device, format, useCommunicationsMode),
            device,
            format)
    {
    }

    internal WindowsWasapiCapture(IWindowsWasapiRecorder recorder, PcmAudioFormat format)
        : this(recorder, device: null, format)
    {
    }

    private WindowsWasapiCapture(
        IWindowsWasapiRecorder recorder,
        IDisposable? device,
        PcmAudioFormat format)
    {
        this.recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        this.device = device;
        Format = format ?? throw new ArgumentNullException(nameof(format));
        recorder.DataAvailable += HandleDataAvailable;
        recorder.RecordingStopped += HandleRecordingStopped;
    }

    private static IWindowsWasapiRecorder BuildRecorder(
        MMDevice device,
        PcmAudioFormat format,
        bool useCommunicationsMode)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);
        WasapiRecorderBuilder builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(BufferLengthMilliseconds)
            .WithFormat(new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels))
            .WithMmcssThreadPriority(MmcssTaskName);
        if (useCommunicationsMode)
            builder.WithCommunicationsMode();

        return new WindowsWasapiRecorderAdapter(builder.Build());
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;

    public PcmAudioFormat Format { get; }

    public bool IsRunning
    {
        get
        {
            lock (stateSync)
                return running;
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed || disposing, this);
            ThrowIfCaptureFailed();

            lock (stateSync)
            {
                if (running)
                    return;

                stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                running = true;
            }

            try
            {
                recorder.StartRecording();
            }
            catch
            {
                lock (stateSync)
                {
                    running = false;
                    stopped.TrySetResult();
                }
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? completion;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            completion = RequestStop();
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (completion is not null)
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfCaptureFailed();
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || disposing)
                return;

            disposing = true;
            Task? completion = RequestStop();
            if (completion is not null)
                await completion.ConfigureAwait(false);

            recorder.DataAvailable -= HandleDataAvailable;
            recorder.RecordingStopped -= HandleRecordingStopped;
            try
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                device?.Dispose();
                disposed = true;
            }
        }
        finally
        {
            disposing = false;
            lifecycleGate.Release();
        }
    }

    private Task? RequestStop()
    {
        Task? completion;
        lock (stateSync)
        {
            if (!running)
                return null;

            running = false;
            completion = stopped?.Task;
        }

        recorder.StopRecording();
        return completion;
    }

    private void HandleDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        _ = flags;
        _ = devicePosition;
        _ = qpcPosition;

        lock (stateSync)
        {
            if (!running || disposing || disposed || buffer.IsEmpty)
                return;
        }

        short[] samples = CopyPcm16Samples(buffer);
        lock (stateSync)
        {
            if (!running || disposing || disposed)
                return;
        }
        SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }

    internal static short[] CopyPcm16Samples(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length % sizeof(short) != 0)
            throw new InvalidDataException("A PCM16 capture packet must contain complete samples.");
        return MemoryMarshal.Cast<byte, short>(buffer).ToArray();
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs args)
    {
        TaskCompletionSource? completion;
        lock (stateSync)
        {
            running = false;
            if (!disposing && !disposed && args.Exception is not null)
                captureFailure ??= args.Exception;
            completion = stopped;
        }

        completion?.TrySetResult();
    }

    private void ThrowIfCaptureFailed()
    {
        Exception? failure;
        lock (stateSync)
            failure = captureFailure;
        if (failure is not null)
            throw new IOException("Windows audio capture stopped unexpectedly.", failure);
    }
}

internal interface IWindowsWasapiRecorder : IAsyncDisposable
{
    event CaptureDataAvailableHandler? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsWasapiRecorderAdapter(WasapiRecorder recorder) : IWindowsWasapiRecorder
{
    public event CaptureDataAvailableHandler? DataAvailable
    {
        add => recorder.DataAvailable += value;
        remove => recorder.DataAvailable -= value;
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => recorder.RecordingStopped += value;
        remove => recorder.RecordingStopped -= value;
    }

    public void StartRecording() => recorder.StartRecording();
    public void StopRecording() => recorder.StopRecording();
    public ValueTask DisposeAsync() => recorder.DisposeAsync();
}
