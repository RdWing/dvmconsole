// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal interface IPipeWireApi : IDisposable
{
    int GetDeviceCount(int input, out int count);
    int GetDevice(int input, int index, out ulong deviceId, byte[] name, int capacity, out int isDefault);
    int IsBluetoothDevice(ulong deviceId);
    SafePipeWireStreamHandle CreateStream(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample);
    int StartStream(SafePipeWireStreamHandle stream);
    int StopStream(SafePipeWireStreamHandle stream);
    int GetSampleRate(SafePipeWireStreamHandle stream);
    int ReadStream(SafePipeWireStreamHandle stream, short[] samples, int capacity);
    int WaitForCapture(SafePipeWireStreamHandle stream, int timeoutMilliseconds);
    void WakeCapture(SafePipeWireStreamHandle stream);
    int WriteStream(SafePipeWireStreamHandle stream, short[] samples, int count);
    uint GetQueuedSamples(SafePipeWireStreamHandle stream);
    ulong GetStarvedSamples(SafePipeWireStreamHandle stream);
    ulong GetPendingStarvedSamples(SafePipeWireStreamHandle stream);
    ulong GetOutputCallbackCount(SafePipeWireStreamHandle stream);
    void EndPlaybackContinuity(SafePipeWireStreamHandle stream);
}

internal sealed class NativePipeWireApi : IPipeWireApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceCountDelegate(int input, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceDelegate(int input, int index, out ulong deviceId, byte[] name, int capacity, out int isDefault);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsBluetoothDeviceDelegate(ulong deviceId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateStreamDelegate(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamStatusDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSampleRateDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamReadDelegate(IntPtr stream, [Out] short[] samples, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWaitDelegate(IntPtr stream, int timeoutMilliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StreamWriteDelegate(IntPtr stream, short[] samples, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StreamQueuedSamplesDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong StreamCounterDelegate(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StreamActionDelegate(IntPtr stream);

    private readonly IntPtr library;
    private readonly GetDeviceCountDelegate getDeviceCount;
    private readonly GetDeviceDelegate getDevice;
    private readonly IsBluetoothDeviceDelegate isBluetoothDevice;
    private readonly CreateStreamDelegate createStream;
    private readonly StreamStatusDelegate startStream;
    private readonly StreamStatusDelegate stopStream;
    private readonly GetSampleRateDelegate getSampleRate;
    private readonly StreamReadDelegate readStream;
    private readonly StreamWaitDelegate waitForCapture;
    private readonly StreamActionDelegate wakeCapture;
    private readonly StreamWriteDelegate writeStream;
    private readonly StreamQueuedSamplesDelegate queuedSamples;
    private readonly StreamCounterDelegate starvedSamples;
    private readonly StreamCounterDelegate pendingStarvedSamples;
    private readonly StreamCounterDelegate outputCallbackCount;
    private readonly StreamActionDelegate endPlaybackContinuity;
    private readonly StreamActionDelegate destroyStream;
    private readonly object lifetimeSync = new();
    private int openStreams;
    private bool disposeRequested;
    private bool libraryFreed;

    private NativePipeWireApi(IntPtr library)
    {
        this.library = library;
        getDeviceCount = Get<GetDeviceCountDelegate>("dvm_audio_get_device_count");
        getDevice = Get<GetDeviceDelegate>("dvm_audio_get_device");
        isBluetoothDevice = Get<IsBluetoothDeviceDelegate>("dvm_audio_device_is_bluetooth");
        createStream = Get<CreateStreamDelegate>("dvm_audio_stream_create");
        startStream = Get<StreamStatusDelegate>("dvm_audio_stream_start");
        stopStream = Get<StreamStatusDelegate>("dvm_audio_stream_stop");
        getSampleRate = Get<GetSampleRateDelegate>("dvm_audio_stream_get_sample_rate");
        readStream = Get<StreamReadDelegate>("dvm_audio_stream_read");
        waitForCapture = Get<StreamWaitDelegate>("dvm_audio_stream_wait_for_capture");
        wakeCapture = Get<StreamActionDelegate>("dvm_audio_stream_wake_capture");
        writeStream = Get<StreamWriteDelegate>("dvm_audio_stream_write");
        queuedSamples = Get<StreamQueuedSamplesDelegate>("dvm_audio_stream_queued_samples");
        starvedSamples = Get<StreamCounterDelegate>("dvm_audio_stream_starved_samples");
        pendingStarvedSamples = Get<StreamCounterDelegate>("dvm_audio_stream_pending_starved_samples");
        outputCallbackCount = Get<StreamCounterDelegate>("dvm_audio_stream_output_callback_count");
        endPlaybackContinuity = Get<StreamActionDelegate>("dvm_audio_stream_end_playback_continuity");
        destroyStream = Get<StreamActionDelegate>("dvm_audio_stream_destroy");
    }

    public static NativePipeWireApi Load(string? configuredPath)
    {
        string? path = configuredPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable("DVM_PIPEWIRE_AUDIO_LIBRARY");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "libdvmaudio-pipewire.so");

        string fullPath = Path.GetFullPath(path);
        IntPtr library = NativeLibrary.Load(fullPath);
        try
        {
            PipeWireNativeContract.ValidateExports(library);
            return new NativePipeWireApi(library);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public int GetDeviceCount(int input, out int count) => getDeviceCount(input, out count);
    public int GetDevice(int input, int index, out ulong deviceId, byte[] name, int capacity, out int isDefault)
        => getDevice(input, index, out deviceId, name, capacity, out isDefault);
    public int IsBluetoothDevice(ulong deviceId) => isBluetoothDevice(deviceId);
    public SafePipeWireStreamHandle CreateStream(ulong deviceId, int input, int sampleRate, int channels, int bitsPerSample)
    {
        lock (lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            IntPtr stream = createStream(deviceId, input, sampleRate, channels, bitsPerSample);
            if (stream != IntPtr.Zero)
                openStreams++;
            return new(stream, ReleaseStream);
        }
    }
    public int StartStream(SafePipeWireStreamHandle stream) => Invoke(stream, handle => startStream(handle));
    public int StopStream(SafePipeWireStreamHandle stream) => Invoke(stream, handle => stopStream(handle));
    public int GetSampleRate(SafePipeWireStreamHandle stream) => Invoke(stream, handle => getSampleRate(handle));
    public int ReadStream(SafePipeWireStreamHandle stream, short[] samples, int capacity)
        => Invoke(stream, handle => readStream(handle, samples, capacity));
    public int WaitForCapture(SafePipeWireStreamHandle stream, int timeoutMilliseconds)
        => Invoke(stream, handle => waitForCapture(handle, timeoutMilliseconds));
    public void WakeCapture(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => wakeCapture(handle));
    public int WriteStream(SafePipeWireStreamHandle stream, short[] samples, int count)
        => Invoke(stream, handle => writeStream(handle, samples, count));
    public uint GetQueuedSamples(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => queuedSamples(handle));
    public ulong GetStarvedSamples(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => starvedSamples(handle));
    public ulong GetPendingStarvedSamples(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => pendingStarvedSamples(handle));
    public ulong GetOutputCallbackCount(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => outputCallbackCount(handle));
    public void EndPlaybackContinuity(SafePipeWireStreamHandle stream)
        => Invoke(stream, handle => endPlaybackContinuity(handle));

    public void Dispose()
    {
        lock (lifetimeSync)
        {
            disposeRequested = true;
            FreeLibraryWhenUnused();
        }
    }

    private T Get<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static T Invoke<T>(SafePipeWireStreamHandle stream, Func<IntPtr, T> action)
    {
        bool addedReference = false;
        try
        {
            stream.DangerousAddRef(ref addedReference);
            return action(stream.DangerousGetHandle());
        }
        finally
        {
            if (addedReference)
                stream.DangerousRelease();
        }
    }

    private static void Invoke(SafePipeWireStreamHandle stream, Action<IntPtr> action)
        => Invoke(stream, handle =>
        {
            action(handle);
            return 0;
        });

    private void ReleaseStream(IntPtr stream)
    {
        lock (lifetimeSync)
        {
            destroyStream(stream);
            openStreams--;
            FreeLibraryWhenUnused();
        }
    }

    private void FreeLibraryWhenUnused()
    {
        if (!disposeRequested || openStreams != 0 || libraryFreed)
            return;
        NativeLibrary.Free(library);
        libraryFreed = true;
    }
}

internal sealed class SafePipeWireStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<IntPtr>? destroy;

    public SafePipeWireStreamHandle(IntPtr handle, Action<IntPtr> destroy)
        : base(ownsHandle: true)
    {
        this.destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        destroy?.Invoke(handle);
        return true;
    }
}
