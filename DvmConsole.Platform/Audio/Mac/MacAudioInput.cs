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
    /// AudioQueue-backed PCM capture. Native callbacks only copy into the
    /// preallocated SPSC ring and re-enqueue the native buffer; managed delivery
    /// runs on a separate task.
    /// </summary>
    public sealed class MacAudioInput : IAudioInput, IAsyncDisposable
    {
        private static readonly CoreAudioNative.AudioQueueInputCallback InputCallback = OnInputCallback;
        private static readonly CoreAudioNative.AudioObjectPropertyListenerProc DeviceListener = OnDevicePropertyChanged;

        private readonly MacAudioDeviceDescriptor _descriptor;
        private readonly MacAudioPcmRing _ring;
        private readonly int _queueBufferBytes;
        private readonly IntPtr _listenerData;
        private readonly object _stateGate = new();
        private int _started;
        private int _stopRequested;
        private int _deviceLost;
        private int _listenerRegistered;
        private int _handleReleased;
        private int _disposed;
        private int _callbackCount;
        private IntPtr _queue;
        private Task<AudioStreamEnd>? _runTask;

        internal MacAudioInput(MacAudioDeviceDescriptor descriptor, PcmFormat format)
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("The CoreAudio input is available only on macOS.");
            }

            _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Format = format;
            _ = CoreAudioNative.CreatePcmDescription(format);
            _queueBufferBytes = Math.Max(AudioPcm.BlockBytes, Math.Max(format.BytesPerSecond / 10, format.BytesPerSample * format.Channels));
            _ring = new MacAudioPcmRing(Math.Max(format.BytesPerSecond * 10, _queueBufferBytes * 8));

            var handle = GCHandle.Alloc(this);
            _listenerData = GCHandle.ToIntPtr(handle);
        }

        public AudioDeviceInfo Device => _descriptor.Info;

        public PcmFormat Format { get; }

        public Task<AudioStreamEnd> StartAsync(
            Func<ReadOnlyMemory<byte>, Task> onData,
            CancellationToken cancellationToken)
        {
            if (onData is null)
            {
                throw new ArgumentNullException(nameof(onData));
            }

            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(MacAudioInput));
                }

                if (Interlocked.Exchange(ref _started, 1) != 0)
                {
                    throw new InvalidOperationException("An audio input can only be started once.");
                }

                try
                {
                    OpenQueue();
                    _runTask = PumpAsync(onData, cancellationToken);
                    return _runTask;
                }
                catch
                {
                    Interlocked.Exchange(ref _started, 0);
                    DisposeNativeResources();
                    throw;
                }
            }
        }

        public Task StopAsync()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            lock (_stateGate)
            {
                var queue = Volatile.Read(ref _queue);
                if (queue != IntPtr.Zero)
                {
                    _ = CoreAudioNative.AudioQueueStop(queue, true);
                }
            }

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
            var status = CoreAudioNative.AudioQueueNewInput(
                ref format,
                InputCallback,
                _listenerData,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                out var queue);
            CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not open the input device");
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
                    CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not allocate an input buffer");
                    status = CoreAudioNative.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                    CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not enqueue an input buffer");
                }

                status = CoreAudioNative.AudioQueueStart(queue, IntPtr.Zero);
                CoreAudioNative.ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not start the input device");
            }
            catch
            {
                DisposeNativeResources();
                throw;
            }
        }

        private async Task<AudioStreamEnd> PumpAsync(
            Func<ReadOnlyMemory<byte>, Task> onData,
            CancellationToken cancellationToken)
        {
            var deliveryBuffer = new byte[_queueBufferBytes];
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return AudioStreamEnd.Cancelled();
                    }

                    if (Volatile.Read(ref _deviceLost) != 0)
                    {
                        return AudioStreamEnd.DeviceLost();
                    }

                    if (Volatile.Read(ref _stopRequested) != 0)
                    {
                        return AudioStreamEnd.Requested();
                    }

                    var count = _ring.ReadInto(deliveryBuffer, deliveryBuffer.Length);
                    if (count > 0)
                    {
                        try
                        {
                            await onData(new ReadOnlyMemory<byte>(deliveryBuffer, 0, count)).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            return AudioStreamEnd.Error(AudioDeviceErrorKind.ReadFailed, exception.Message);
                        }

                        continue;
                    }

                    try
                    {
                        await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return AudioStreamEnd.Cancelled();
                    }
                }
            }
            finally
            {
                DisposeNativeResources();
            }
        }

        private static void OnInputCallback(
            IntPtr userData,
            IntPtr queue,
            IntPtr buffer,
            IntPtr startTime,
            uint numberPacketDescriptions,
            IntPtr packetDescriptions)
        {
            if (userData == IntPtr.Zero || buffer == IntPtr.Zero)
            {
                return;
            }

            MacAudioInput? input;
            try
            {
                input = (MacAudioInput?)GCHandle.FromIntPtr(userData).Target;
            }
            catch (Exception)
            {
                // The user-data handle was freed during teardown before this
                // callback could resolve it; the queue is being disposed, so
                // there is nothing left to do.
                return;
            }

            if (input is null)
            {
                return;
            }

            // Count this callback so teardown can wait for in-flight callbacks
            // to exit before disposing the queue and freeing the user-data
            // handle. Callbacks never acquire _stateGate, so the teardown wait
            // cannot deadlock.
            Interlocked.Increment(ref input._callbackCount);
            try
            {
                if (Volatile.Read(ref input._stopRequested) != 0)
                {
                    return;
                }

                var data = CoreAudioNative.GetAudioQueueBufferData(buffer);
                var byteCount = checked((int)CoreAudioNative.GetAudioQueueBufferByteSize(buffer));
                if (data != IntPtr.Zero && byteCount > 0)
                {
                    _ = input._ring.TryWriteFromNative(data, byteCount);
                }

                if (Volatile.Read(ref input._stopRequested) == 0
                    && Volatile.Read(ref input._queue) != IntPtr.Zero)
                {
                    _ = CoreAudioNative.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                }
            }
            catch (Exception)
            {
                // Native callbacks cannot propagate exceptions across the ABI.
                // Do not swallow silently: a malformed or transient callback
                // consumes a queue buffer without re-enqueueing it, which
                // starves the four-buffer queue and leaves the pump spinning
                // forever with no data. Mark the device lost so the pump ends
                // and tears the queue down.
                Volatile.Write(ref input._deviceLost, 1);
                Interlocked.Exchange(ref input._stopRequested, 1);
            }
            finally
            {
                Interlocked.Decrement(ref input._callbackCount);
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
                var input = (MacAudioInput?)GCHandle.FromIntPtr(clientData).Target;
                if (input is not null)
                {
                    Volatile.Write(ref input._deviceLost, 1);
                    Interlocked.Exchange(ref input._stopRequested, 1);
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
            // In-flight callbacks are short (a ring copy plus a re-enqueue) and
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
