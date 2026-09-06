// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DvmConsole.Ptt;

// XRecord observes X11 device events without grabbing or swallowing the key.
// Wayland sessions use the desktop portal adapter instead of this X11 path.
internal sealed partial class LinuxX11GlobalKeyboardCapture : IGlobalKeyboardCapture
{
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int RecordFromServer = 0;
    private const int RecordStartOfData = 4;
    private const nuint RecordAllClients = 3;

    private readonly object sync = new();
    private readonly ManualResetEventSlim ready = new(false);
    private readonly RecordInterceptCallback callback;
    private Thread? recordThread;
    private IntPtr controlDisplay;
    private IntPtr dataDisplay;
    private nuint context;
    private Exception? startException;
    private bool started;
    private bool stopping;
    private bool disposed;

    public LinuxX11GlobalKeyboardCapture()
    {
        callback = HandleRecordData;
    }

    public event Action<KeyboardPttKey, bool>? KeyChanged;
    public event Action<Exception>? Terminated;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux X11 global keyboard capture is unavailable here.");

        lock (sync)
        {
            if (started)
                return ValueTask.CompletedTask;
            InitializeContext();
            startException = null;
            stopping = false;
            ready.Reset();
            recordThread = new Thread(RunRecordLoop)
            {
                IsBackground = true,
                Name = "DVM Console X11 global PTT"
            };
            recordThread.Start();
        }

        if (!ready.Wait(TimeSpan.FromSeconds(5), cancellationToken))
        {
            StopRecordLoop();
            throw new TimeoutException("The Linux X11 global PTT recorder did not start in time.");
        }
        if (startException is not null)
        {
            StopRecordLoop();
            ExceptionDispatchInfo.Capture(startException).Throw();
        }

        started = true;
        return ValueTask.CompletedTask;
    }

    public void Stop()
    {
        if (!started && recordThread is null)
            return;
        StopRecordLoop();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        try
        {
            StopRecordLoop();
        }
        finally
        {
            ready.Dispose();
            disposed = true;
        }
    }

    private void InitializeContext()
    {
        try
        {
            if (XInitThreads() == 0)
                throw new InvalidOperationException("XInitThreads failed for Linux global PTT capture.");

            controlDisplay = XOpenDisplay(IntPtr.Zero);
            dataDisplay = XOpenDisplay(IntPtr.Zero);
            if (controlDisplay == IntPtr.Zero || dataDisplay == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Could not open the X11 display for global PTT. This adapter requires the X11 desktop host and a valid DISPLAY session.");
            }

            if (XRecordQueryVersion(controlDisplay, out _, out _) == 0)
                throw new NotSupportedException("The X11 RECORD extension is unavailable for global PTT capture.");

            _ = XkbSetDetectableAutoRepeat(dataDisplay, 1, out _);
            IntPtr rangePointer = XRecordAllocRange();
            if (rangePointer == IntPtr.Zero)
                throw new OutOfMemoryException("XRecordAllocRange failed for global PTT capture.");
            try
            {
                var range = new XRecordRange
                {
                    DeviceEvents = new XRecordRange8 { First = KeyPress, Last = KeyRelease }
                };
                Marshal.StructureToPtr(range, rangePointer, fDeleteOld: false);
                nuint[] clients = [RecordAllClients];
                IntPtr[] ranges = [rangePointer];
                context = XRecordCreateContext(
                    controlDisplay,
                    datumFlags: 0,
                    clients,
                    clients.Length,
                    ranges,
                    ranges.Length);
                if (context != 0)
                    _ = XSync(controlDisplay, discard: 0);
            }
            finally
            {
                _ = XFree(rangePointer);
            }

            if (context == 0)
                throw new InvalidOperationException("XRecordCreateContext failed for global PTT capture.");
        }
        catch
        {
            ReleaseNativeState();
            throw;
        }
    }

    private void RunRecordLoop()
    {
        try
        {
            int status = XRecordEnableContext(dataDisplay, context, callback, IntPtr.Zero);
            if (status == 0 && !stopping)
                startException = new Win32Exception("XRecordEnableContext failed for global PTT capture.");
        }
        catch (Exception exception)
        {
            startException = exception;
        }
        finally
        {
            ready.Set();
            if (!stopping)
                Terminated?.Invoke(startException ?? new IOException("The X11 global PTT recorder stopped."));
        }
    }

    private void StopRecordLoop()
    {
        Thread? thread;
        lock (sync)
        {
            thread = recordThread;
            stopping = true;
            started = false;
        }

        if (controlDisplay != IntPtr.Zero && context != 0)
        {
            _ = XRecordDisableContext(controlDisplay, context);
            _ = XSync(controlDisplay, discard: 0);
        }

        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Linux X11 global PTT recorder did not stop in time.");

        lock (sync)
        {
            recordThread = null;
            ReleaseNativeState();
            stopping = false;
        }
    }

    private void HandleRecordData(IntPtr closure, IntPtr dataPointer)
    {
        _ = closure;
        if (dataPointer == IntPtr.Zero)
            return;

        try
        {
            XRecordInterceptData data = Marshal.PtrToStructure<XRecordInterceptData>(dataPointer);
            if (data.Category == RecordStartOfData)
            {
                ready.Set();
                return;
            }
            if (data.Category != RecordFromServer || data.Data == IntPtr.Zero || data.DataLength == 0)
                return;

            int eventType = Marshal.ReadByte(data.Data) & 0x7F;
            if (eventType is not (KeyPress or KeyRelease))
                return;

            byte keyCode = Marshal.ReadByte(data.Data, 1);
            nuint keySym = XkbKeycodeToKeysym(controlDisplay, keyCode, group: 0, level: 0);
            if (KeyboardPttKeyMapping.TryFromX11KeySym(keySym, out KeyboardPttKey key))
                KeyChanged?.Invoke(key, eventType == KeyPress);
        }
        finally
        {
            XRecordFreeData(dataPointer);
        }
    }

    private void ReleaseNativeState()
    {
        if (controlDisplay != IntPtr.Zero && context != 0)
        {
            _ = XRecordFreeContext(controlDisplay, context);
            context = 0;
        }
        if (dataDisplay != IntPtr.Zero)
        {
            _ = XCloseDisplay(dataDisplay);
            dataDisplay = IntPtr.Zero;
        }
        if (controlDisplay != IntPtr.Zero)
        {
            _ = XCloseDisplay(controlDisplay);
            controlDisplay = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRecordRange8
    {
        public byte First;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRecordRange16
    {
        public ushort First;
        public ushort Last;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRecordExtRange
    {
        public XRecordRange8 Major;
        public XRecordRange16 Minor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRecordRange
    {
        public XRecordRange8 CoreRequests;
        public XRecordRange8 CoreReplies;
        public XRecordExtRange ExtensionRequests;
        public XRecordExtRange ExtensionReplies;
        public XRecordRange8 DeliveredEvents;
        public XRecordRange8 DeviceEvents;
        public XRecordRange8 Errors;
        public int ClientStarted;
        public int ClientDied;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRecordInterceptData
    {
        public nuint IdBase;
        public nuint ServerTime;
        public nuint ClientSequence;
        public int Category;
        public int ClientSwapped;
        public IntPtr Data;
        public nuint DataLength;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RecordInterceptCallback(IntPtr closure, IntPtr data);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XInitThreads();

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr XOpenDisplay(IntPtr displayName);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XSync(IntPtr display, int discard);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XFree(IntPtr data);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint XkbKeycodeToKeysym(IntPtr display, byte keyCode, int group, int level);

    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, out int supported);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XRecordQueryVersion(IntPtr display, out int major, out int minor);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr XRecordAllocRange();

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint XRecordCreateContext(
        IntPtr display,
        int datumFlags,
        [MarshalUsing(CountElementName = nameof(clientCount))] nuint[] clients,
        int clientCount,
        [MarshalUsing(CountElementName = nameof(rangeCount))] IntPtr[] ranges,
        int rangeCount);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XRecordEnableContext(
        IntPtr display,
        nuint recordContext,
        RecordInterceptCallback interceptCallback,
        IntPtr closure);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XRecordDisableContext(IntPtr display, nuint recordContext);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XRecordFreeContext(IntPtr display, nuint recordContext);

    [LibraryImport("libXtst.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void XRecordFreeData(IntPtr data);
}
