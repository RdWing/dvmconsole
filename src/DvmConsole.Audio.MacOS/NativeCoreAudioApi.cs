// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

// Exact managed binding table for libdvmaudio. Higher-level route, endpoint,
// and voice-processing policies remain outside this ABI-only component.
internal sealed class NativeCoreAudioApi : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceCountDelegate(int input, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceDelegate(int input, int index, out ulong deviceId, byte[] name, int nameCapacity, out int isDefault);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsBluetoothDeviceDelegate(ulong deviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateStreamDelegate(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamStatusDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSampleRateDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamReadDelegate(IntPtr stream, [Out] short[] samples, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWaitDelegate(IntPtr stream, int timeoutMilliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWriteDelegate(IntPtr stream, short[] samples, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StreamQueuedSamplesDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong StreamStarvedSamplesDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EndPlaybackContinuityDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyStreamDelegate(IntPtr stream);

    private readonly IntPtr handle;
    private readonly GetDeviceCountDelegate getDeviceCount;
    private readonly GetDeviceDelegate getDevice;
    private readonly IsBluetoothDeviceDelegate? isBluetoothDevice;
    private readonly CreateStreamDelegate createStream;
    private readonly StreamStatusDelegate startStream;
    private readonly StreamStatusDelegate stopStream;
    private readonly GetSampleRateDelegate getSampleRate;
    private readonly StreamReadDelegate readStream;
    private readonly StreamWaitDelegate waitForCapture;
    private readonly EndPlaybackContinuityDelegate wakeCapture;
    private readonly StreamWriteDelegate writeStream;
    private readonly StreamQueuedSamplesDelegate queuedSamples;
    private readonly StreamStarvedSamplesDelegate starvedSamples;
    private readonly StreamStarvedSamplesDelegate pendingStarvedSamples;
    private readonly StreamStarvedSamplesDelegate outputCallbackCount;
    private readonly StreamStarvedSamplesDelegate outputPresentationLatencyNanoseconds;
    private readonly EndPlaybackContinuityDelegate endPlaybackContinuity;
    private readonly DestroyStreamDelegate destroyStream;
    private int referenceCount = 1;
    private int ownerDisposed;

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
        waitForCapture = Get<StreamWaitDelegate>("dvm_audio_stream_wait_for_capture");
        wakeCapture = Get<EndPlaybackContinuityDelegate>("dvm_audio_stream_wake_capture");
        writeStream = Get<StreamWriteDelegate>("dvm_audio_stream_write");
        queuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_stream_queued_samples");
        starvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_starved_samples");
        pendingStarvedSamples = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_pending_starved_samples");
        outputCallbackCount = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_output_callback_count");
        outputPresentationLatencyNanoseconds = Get<StreamStarvedSamplesDelegate>("dvm_audio_stream_output_presentation_latency_ns");
        endPlaybackContinuity = Get<EndPlaybackContinuityDelegate>("dvm_audio_stream_end_playback_continuity");
        destroyStream = Get<DestroyStreamDelegate>("dvm_audio_stream_destroy");
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
    public SafeCoreAudioStreamHandle CreateStream(ulong id, int input, int sampleRate, int channels, int bits)
        => CreateOwnedStream(createStream(id, input, sampleRate, channels, bits));
    public int StartStream(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return startStream(lease.Handle);
    }
    public int StopStream(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return stopStream(lease.Handle);
    }
    public int GetSampleRate(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return getSampleRate(lease.Handle);
    }
    public int ReadStream(SafeCoreAudioStreamHandle stream, short[] samples, int capacity)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return readStream(lease.Handle, samples, capacity);
    }
    public int WaitForCapture(SafeCoreAudioStreamHandle stream, int timeoutMilliseconds)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return waitForCapture(lease.Handle, timeoutMilliseconds);
    }
    public void WakeCapture(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        wakeCapture(lease.Handle);
    }
    public int WriteStream(SafeCoreAudioStreamHandle stream, short[] samples, int count)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return writeStream(lease.Handle, samples, count);
    }
    public uint GetQueuedSamples(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return queuedSamples(lease.Handle);
    }
    public ulong GetStarvedSamples(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return starvedSamples(lease.Handle);
    }
    public ulong GetPendingStarvedSamples(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return pendingStarvedSamples(lease.Handle);
    }
    public ulong GetOutputCallbackCount(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return outputCallbackCount(lease.Handle);
    }
    public TimeSpan GetOutputPresentationLatency(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        return NanosecondsToTimeSpan(outputPresentationLatencyNanoseconds(lease.Handle));
    }
    public void EndPlaybackContinuity(SafeCoreAudioStreamHandle stream)
    {
        using var lease = new SafeCoreAudioStreamLease(stream);
        endPlaybackContinuity(lease.Handle);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownerDisposed, 1) == 0)
            ReleaseReference();
    }

    internal void DestroyStream(IntPtr stream) => destroyStream(stream);

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref referenceCount) == 0)
            NativeLibrary.Free(handle);
    }

    private SafeCoreAudioStreamHandle CreateOwnedStream(IntPtr stream)
    {
        if (stream == IntPtr.Zero)
            return new SafeCoreAudioStreamHandle();
        AddReference();
        return new SafeCoreAudioStreamHandle(this, stream);
    }

    private static TimeSpan NanosecondsToTimeSpan(ulong nanoseconds)
    {
        ulong ticks = nanoseconds / 100;
        return ticks > long.MaxValue
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)ticks);
    }

    private void AddReference()
    {
        while (true)
        {
            int current = Volatile.Read(ref referenceCount);
            if (current == 0)
                throw new ObjectDisposedException(nameof(NativeCoreAudioApi));
            if (Interlocked.CompareExchange(ref referenceCount, current + 1, current) == current)
                return;
        }
    }

    private T Get<T>(string symbol) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol));

    private T? TryGet<T>(string symbol) where T : Delegate
        => NativeLibrary.TryGetExport(handle, symbol, out IntPtr address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
}

internal ref struct SafeCoreAudioStreamLease
{
    private SafeCoreAudioStreamHandle? stream;
    private bool addedReference;

    internal SafeCoreAudioStreamLease(SafeCoreAudioStreamHandle stream)
    {
        this.stream = stream;
        addedReference = false;
        stream.DangerousAddRef(ref addedReference);
        Handle = stream.DangerousGetHandle();
    }

    internal IntPtr Handle { get; }

    public void Dispose()
    {
        if (!addedReference)
            return;
        addedReference = false;
        stream!.DangerousRelease();
        stream = null;
    }
}

internal sealed class SafeCoreAudioStreamHandle : SafeHandle
{
    private NativeCoreAudioApi? owner;

    internal SafeCoreAudioStreamHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    internal SafeCoreAudioStreamHandle(
        NativeCoreAudioApi owner,
        IntPtr handle)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        this.owner = owner;
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeCoreAudioApi? currentOwner = owner;
        owner = null;
        if (currentOwner is null)
            return true;
        try
        {
            currentOwner.DestroyStream(handle);
            return true;
        }
        finally
        {
            currentOwner.ReleaseReference();
        }
    }
}
