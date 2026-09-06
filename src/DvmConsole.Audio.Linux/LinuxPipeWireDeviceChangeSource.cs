// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

public sealed class LinuxPipeWireDeviceChangeSource : IAudioDeviceChangeSource
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeChangedCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateMonitorDelegate(NativeChangedCallback callback, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyMonitorDelegate(IntPtr monitor);

    private readonly string? configuredPath;
    private readonly NativeChangedCallback callback;
    private IntPtr library;
    private IntPtr monitor;
    private DestroyMonitorDelegate? destroyMonitor;
    private bool started;
    private bool disposed;

    public LinuxPipeWireDeviceChangeSource(string? configuredPath = null)
    {
        this.configuredPath = configuredPath;
        callback = HandleNativeChanged;
    }

    public event EventHandler? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;

        library = NativeLibrary.Load(ResolveLibraryPath(configuredPath));
        try
        {
            PipeWireNativeContract.ValidateExports(library);
            var createMonitor = Marshal.GetDelegateForFunctionPointer<CreateMonitorDelegate>(
                NativeLibrary.GetExport(library, "dvm_audio_device_monitor_create"));
            destroyMonitor = Marshal.GetDelegateForFunctionPointer<DestroyMonitorDelegate>(
                NativeLibrary.GetExport(library, "dvm_audio_device_monitor_destroy"));
            monitor = createMonitor(callback, IntPtr.Zero);
            if (monitor == IntPtr.Zero)
                throw new InvalidOperationException("Unable to start the PipeWire device monitor.");
            started = true;
        }
        catch
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
            destroyMonitor = null;
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (monitor != IntPtr.Zero)
        {
            destroyMonitor?.Invoke(monitor);
            monitor = IntPtr.Zero;
        }
        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }
        destroyMonitor = null;
    }

    private void HandleNativeChanged(IntPtr userData)
    {
        _ = userData;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ResolveLibraryPath(string? configuredPath)
    {
        string? path = configuredPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable("DVM_PIPEWIRE_AUDIO_LIBRARY");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "libdvmaudio-pipewire.so");
        return Path.GetFullPath(path);
    }
}
