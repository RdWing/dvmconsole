// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal sealed class RemoteIoDevice : SafeHandle
{
    internal RemoteIoDevice() : base(IntPtr.Zero, ownsHandle: true) { }
    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static RemoteIoDevice Create(int sampleRate, bool input)
    {
        int status = Native.Create(checked((uint)sampleRate), input ? 1 : 0, out IntPtr pointer);
        if (status != 0) throw new IOException($"RemoteIO creation failed ({status}).");
        var device = new RemoteIoDevice();
        device.SetHandle(pointer);
        return device;
    }

    internal void Start()
    {
        int status = Native.Start(this);
        if (status != 0) throw new IOException($"RemoteIO start failed ({status}).");
    }

    internal void SetOutputEnabled(bool enabled) => Native.SetOutputEnabled(this, enabled ? 1 : 0);
    internal void StopImmediately() => Native.StopImmediately(this);
    internal uint QueuedOutput => Native.QueuedOutput(this);
    internal ulong CallbackCount => Native.CallbackCount(this);
    internal int Error => Native.Error(this);
    internal ulong DroppedInputSamples => Native.DroppedInputSamples(this);
    internal ulong StarvedSamples => Native.StarvedSamples(this);
    internal ulong PendingStarvedSamples => Native.PendingStarvedSamples(this);
    internal void EndExpectedPlayback() => Native.EndExpectedPlayback(this);
    internal unsafe int Write(ReadOnlySpan<short> samples)
    {
        fixed (short* pointer = samples) return checked((int)Native.Write(this, pointer, checked((uint)samples.Length)));
    }
    internal unsafe int Read(Span<short> samples)
    {
        fixed (short* pointer = samples) return checked((int)Native.Read(this, pointer, checked((uint)samples.Length)));
    }

    /// <summary>One capture worker owns the wait and its native lifetime lease.</summary>
    internal sealed class CaptureSignal : IDisposable
    {
        private readonly RemoteIoDevice device;
        private readonly CancellationTokenRegistration cancellation;
        private bool retained;

        internal CaptureSignal(RemoteIoDevice device, CancellationToken token)
        {
            this.device = device;
            device.DangerousAddRef(ref retained);
            try
            {
                cancellation = token.UnsafeRegister(static state =>
                    Native.WakeInput(((RemoteIoDevice)state!).DangerousGetHandle()), device);
            }
            catch { device.DangerousRelease(); retained = false; throw; }
        }

        public bool Wait()
        {
            // This call intentionally blocks the dedicated capture worker, never
            // the session lock or a thread-pool worker. Cancellation wakes it.
            int result = Native.WaitForInput(device.DangerousGetHandle(), 1000);
            if (result < 0) throw new IOException("RemoteIO capture notification failed.");
            return result > 0;
        }

        public void Dispose()
        {
            cancellation.Dispose();
            if (retained) { retained = false; device.DangerousRelease(); }
        }
    }

    protected override bool ReleaseHandle() { Native.Destroy(handle); return true; }

    private static unsafe class Native
    {
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_wait_input", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WaitForInput(IntPtr device, int timeoutMilliseconds);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_wake_input", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void WakeInput(IntPtr device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(uint rate, int input, out IntPtr device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_start", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Start(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_set_output_enabled", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetOutputEnabled(RemoteIoDevice device, int enabled);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_stop_immediately", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void StopImmediately(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Write(RemoteIoDevice device, short* samples, uint count);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint Read(RemoteIoDevice device, short* samples, uint count);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_queued_output", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint QueuedOutput(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_callback_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong CallbackCount(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_dropped_input", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong DroppedInputSamples(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_starved_samples", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong StarvedSamples(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_pending_starved_samples", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong PendingStarvedSamples(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_end_playback_continuity", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void EndExpectedPlayback(RemoteIoDevice device);
        [DllImport("__Internal", EntryPoint = "dvm_ios_audio_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Error(RemoteIoDevice device);
    }
}
