// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// AudioQueue-backed PCM playback. Queue buffers are kept full by the native
    /// callback from a bounded SPSC ring; silence is rendered when the application
    /// has not supplied enough data for the next hardware buffer.
    /// </summary>
    public sealed class MacAudioOutput : IAudioOutput, IAsyncDisposable
    {
        private static readonly CoreAudioNative.AudioQueueOutputCallback OutputCallback = OnOutputCallback;
        private static readonly CoreAudioNative.AudioObjectPropertyListenerProc DeviceListener = OnDevicePropertyChanged;

        private readonly MacAudioDeviceDescriptor _descriptor;
        private readonly MacAudioPcmRing _ring;
        private readonly byte[] _silence;
        private readonly int _queueBufferBytes;
        private readonly IntPtr _listenerData;
        private readonly object _stateGate = new();
        private float _volume = 1.0f;
        private int _stopRequested;
        private int _deviceLost;
        private int _listenerRegistered;
        private int _handleReleased;
        private int _disposed;
        private int _callbackCount;
        private IntPtr _queue;

        internal MacAudioOutput(MacAudioDeviceDescriptor descriptor, PcmFormat format)
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("The CoreAudio output is available only on macOS.");
            }

            _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Format = format;
            _ = CoreAudioNative.CreatePcmDescription(format);
            _queueBufferBytes = Math.Max(AudioPcm.BlockBytes, Math.Max(format.BytesPerSecond / 10, format.BytesPerSample * format.Channels));
            _ring = new MacAudioPcmRing(Math.Max(format.BytesPerSecond * 10, _queueBufferBytes * 8));
            _silence = new byte[_queueBufferBytes];

            var handle = GCHandle.Alloc(this);
            _listenerData = GCHandle.ToIntPtr(handle);
            try
            {
                OpenQueue();
            }
            catch
            {
                DisposeNativeResources();
                throw;
            }
        }

        public AudioDeviceInfo Device => _descriptor.Info;

        public PcmFormat Format { get; }

        public float Volume
        {
            get => _volume;
            set
            {
                var clamped = Math.Clamp(value, 0.0f, 1.0f);
                // Serialize the queue read and the parameter write with
                // disposal: DisposeNativeResources exchanges _queue to zero
                // and disposes the queue under _stateGate, so taking the gate
                // here guarantees the queue pointer is never used after it
                // has been handed to AudioQueueDispose. Callbacks never
                // acquire _stateGate, so this cannot deadlock with the
                // quiescence wait.
                lock (_stateGate)
                {
                    _volume = clamped;
                    var queue = Volatile.Read(ref _queue);
                    if (queue != IntPtr.Zero && Volatile.Read(ref _stopRequested) == 0)
                    {
                        _ = CoreAudioNative.AudioQueueSetParameter(queue, CoreAudioNative.AudioQueueParameterVolume, clamped);
                    }
                }
            }
        }

        public AudioWriteResult Write(ReadOnlyMemory<byte> data)
        {
            if (Volatile.Read(ref _deviceLost) != 0)
            {
                return new AudioWriteResult(AudioWriteStatus.DeviceLost, _ring.BufferedBytes);
            }

            if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _queue) == IntPtr.Zero)
            {
                return new AudioWriteResult(AudioWriteStatus.NotStarted, _ring.BufferedBytes);
            }

            if (!_ring.TryWrite(data))
            {
                return new AudioWriteResult(AudioWriteStatus.BufferOverflow, _ring.BufferedBytes);
            }

            return new AudioWriteResult(AudioWriteStatus.Accepted, _ring.BufferedBytes);
        }

        public void ClearBuffer() => _ring.Clear();

        public Task StopAsync()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            DisposeNativeResources();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            DisposeNativeResources();
            return ValueTask.CompletedTask;
        }

        private void OpenQueue()
        {
            var format = CoreAudioNative.CreatePcmDescription(Format);
            var status = CoreAudioNative.AudioQueueNewOutput(
                ref format,
                OutputCallback,
                _listenerData,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                out var queue);
            CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not open the output device");
            Volatile.Write(ref _queue, queue);

            try
            {
                CoreAudioNative.SetQueueCurrentDevice(queue, _descriptor.Uid);
                var listenerStatus = CoreAudioNative.AddDeviceAliveListener(_descriptor.NativeId, DeviceListener, _listenerData);
                CoreAudioNative.ThrowIfError(
                    listenerStatus,
                    AudioDeviceErrorKind.OpenFailed,
                    "CoreAudio could not register the device-alive listener");
                Volatile.Write(ref _listenerRegistered, 1);

                for (var i = 0; i < 4; i++)
                {
                    status = CoreAudioNative.AudioQueueAllocateBuffer(queue, (uint)_queueBufferBytes, out var buffer);
                    CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not allocate an output buffer");
                    FillBuffer(buffer);
                    status = CoreAudioNative.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                    CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not enqueue an output buffer");
                }

                status = CoreAudioNative.AudioQueueSetParameter(queue, CoreAudioNative.AudioQueueParameterVolume, _volume);
                CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not set output volume");
                status = CoreAudioNative.AudioQueueStart(queue, IntPtr.Zero);
                CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not start the output device");
            }
            catch
            {
                DisposeNativeResources();
                throw;
            }
        }

        private void FillBuffer(IntPtr buffer)
        {
            var nativeData = CoreAudioNative.GetAudioQueueBufferData(buffer);
            var capacity = checked((int)CoreAudioNative.GetAudioQueueBufferCapacity(buffer));
            var count = nativeData == IntPtr.Zero ? 0 : _ring.ReadToNative(nativeData, capacity);
            if (nativeData != IntPtr.Zero && count < capacity)
            {
                Marshal.Copy(_silence, count, IntPtr.Add(nativeData, count), capacity - count);
            }

            CoreAudioNative.SetAudioQueueBufferByteSize(buffer, (uint)capacity);
        }

        private static void OnOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer)
        {
            if (userData == IntPtr.Zero || buffer == IntPtr.Zero)
            {
                return;
            }

            MacAudioOutput? output;
            try
            {
                output = (MacAudioOutput?)GCHandle.FromIntPtr(userData).Target;
            }
            catch (Exception)
            {
                // The user-data handle was freed during teardown before this
                // callback could resolve it; the queue is being disposed, so
                // there is nothing left to do.
                return;
            }

            if (output is null)
            {
                return;
            }

            // Count this callback so teardown can wait for in-flight callbacks
            // to exit before disposing the queue and freeing the user-data
            // handle. Callbacks never acquire _stateGate, so the teardown wait
            // cannot deadlock.
            Interlocked.Increment(ref output._callbackCount);
            try
            {
                if (Volatile.Read(ref output._stopRequested) != 0)
                {
                    return;
                }

                output.FillBuffer(buffer);
                if (Volatile.Read(ref output._stopRequested) == 0
                    && Volatile.Read(ref output._queue) != IntPtr.Zero)
                {
                    _ = CoreAudioNative.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                }
            }
            catch (Exception)
            {
                // Native callbacks cannot propagate exceptions across the ABI.
                // Do not swallow silently: a failed callback consumes a queue
                // buffer without re-enqueueing it, which starves the
                // four-buffer queue and leaves playback stalled. Mark the
                // device lost and request the stop so the stream ends and
                // tears the queue down.
                Volatile.Write(ref output._deviceLost, 1);
                Interlocked.Exchange(ref output._stopRequested, 1);
            }
            finally
            {
                Interlocked.Decrement(ref output._callbackCount);
            }
        }

        private static int OnDevicePropertyChanged(
            uint objectId,
            uint numberAddresses,
            IntPtr addresses,
            IntPtr clientData)
        {
            if (clientData == IntPtr.Zero)
            {
                return CoreAudioNative.AudioHardwareNoError;
            }

            try
            {
                var output = (MacAudioOutput?)GCHandle.FromIntPtr(clientData).Target;
                if (output is not null)
                {
                    Volatile.Write(ref output._deviceLost, 1);
                    Interlocked.Exchange(ref output._stopRequested, 1);
                }
            }
            catch (Exception)
            {
                // Disposal may race with the final HAL callback.
            }

            return CoreAudioNative.AudioHardwareNoError;
        }

        private void DisposeNativeResources()
        {
            // Teardown has begun: no callback may enqueue into the queue after
            // this point, even on paths that do not set the stop flag first.
            Interlocked.Exchange(ref _stopRequested, 1);
            lock (_stateGate)
            {
                var queue = Interlocked.Exchange(ref _queue, IntPtr.Zero);
                if (queue != IntPtr.Zero)
                {
                    // Stop delivery first, then wait for callbacks that were
                    // already executing to exit before disposing the queue, so
                    // a callback that passed the stop check cannot enqueue into
                    // a disposed queue. Callbacks never acquire _stateGate, so
                    // waiting under it cannot deadlock.
                    _ = CoreAudioNative.AudioQueueStop(queue, true);
                    WaitForCallbackQuiescence();
                    _ = CoreAudioNative.AudioQueueDispose(queue, true);
                }
                else
                {
                    // A concurrent disposer already owns the queue teardown;
                    // still wait so the listener is removed and the user-data
                    // handle is freed only after every in-flight callback has
                    // exited.
                    WaitForCallbackQuiescence();
                }

                if (Interlocked.Exchange(ref _listenerRegistered, 0) != 0)
                {
                    _ = CoreAudioNative.RemoveDeviceAliveListener(_descriptor.NativeId, DeviceListener, _listenerData);
                }

                if (_listenerData != IntPtr.Zero && Interlocked.Exchange(ref _handleReleased, 1) == 0)
                {
                    GCHandle.FromIntPtr(_listenerData).Free();
                }

                Volatile.Write(ref _disposed, 1);
            }
        }

        private void WaitForCallbackQuiescence()
        {
            var spin = new SpinWait();
            // In-flight callbacks are short (a ring read plus a re-enqueue) and
            // AudioQueueStop halts new delivery, so this drains immediately in
            // practice. Do not impose a timeout: disposing the queue while a
            // callback is still in native code would recreate the use-after-
            // dispose race this quiescence barrier exists to prevent.
            while (Volatile.Read(ref _callbackCount) != 0)
            {
                spin.SpinOnce();
            }
        }
    }
}
