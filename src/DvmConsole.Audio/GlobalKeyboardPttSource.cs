using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

// Captures the configured PTT key outside the application window when the
// host platform permits it.  The adapter deliberately listens without
// swallowing the key, so the focused-window <see cref="KeyboardPttSource"/>
// remains a safe fallback when native capture is unavailable.
public sealed class GlobalKeyboardPttSource : IPttSource
{
    private readonly KeyboardPttSource stateSource;
    private readonly Func<IGlobalKeyboardCapture> captureFactory;
    private IGlobalKeyboardCapture? capture;
    private bool started;
    private bool disposed;

    public GlobalKeyboardPttSource(KeyboardPttKey activationKey = KeyboardPttKey.Space)
        : this(activationKey, CreateCapture)
    {
    }

    internal GlobalKeyboardPttSource(
        KeyboardPttKey activationKey,
        Func<IGlobalKeyboardCapture> captureFactory)
    {
        ArgumentNullException.ThrowIfNull(captureFactory);
        this.captureFactory = captureFactory;
        stateSource = new KeyboardPttSource(activationKey);
        stateSource.StateChanged += HandleStateChanged;
    }

    public static bool IsPlatformSupported
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public event EventHandler<bool>? StateChanged;
    public bool IsPressed => stateSource.IsPressed;
    public KeyboardPttKey ActivationKey => stateSource.ActivationKey;

    public bool ToggleMode
    {
        get => stateSource.ToggleMode;
        set => stateSource.ToggleMode = value;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
            return;

        await stateSource.StartAsync(cancellationToken).ConfigureAwait(false);
        IGlobalKeyboardCapture? nextCapture = null;
        try
        {
            nextCapture = captureFactory();
            nextCapture.KeyChanged += HandleKeyChanged;
            nextCapture.Start();
            capture = nextCapture;
            started = true;
        }
        catch
        {
            if (nextCapture is not null)
            {
                nextCapture.KeyChanged -= HandleKeyChanged;
                nextCapture.Dispose();
            }

            await stateSource.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!started && capture is null)
            return;

        IGlobalKeyboardCapture? currentCapture = capture;
        capture = null;
        started = false;
        Exception? stopException = null;
        if (currentCapture is not null)
        {
            currentCapture.KeyChanged -= HandleKeyChanged;
            try
            {
                currentCapture.Stop();
            }
            catch (Exception exception)
            {
                stopException = exception;
            }
            finally
            {
                currentCapture.Dispose();
            }
        }

        await stateSource.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (stopException is not null)
            ExceptionDispatchInfo.Capture(stopException).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                stateSource.StateChanged -= HandleStateChanged;
                await stateSource.DisposeAsync().ConfigureAwait(false);
                disposed = true;
            }
        }
    }

    private static IGlobalKeyboardCapture CreateCapture()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsGlobalKeyboardCapture();
        if (OperatingSystem.IsMacOS())
            return new MacGlobalKeyboardCapture();
        throw new PlatformNotSupportedException(
            "OS-global PTT capture is supported on Windows and macOS only.");
    }

    private void HandleKeyChanged(KeyboardPttKey key, bool isDown)
    {
        if (isDown)
            stateSource.HandleKeyDown(key);
        else
            stateSource.HandleKeyUp(key);
    }

    private void HandleStateChanged(object? sender, bool pressed)
        => StateChanged?.Invoke(this, pressed);
}

internal interface IGlobalKeyboardCapture : IDisposable
{
    event Action<KeyboardPttKey, bool>? KeyChanged;
    void Start();
    void Stop();
}

internal sealed class WindowsGlobalKeyboardCapture : IGlobalKeyboardCapture
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x00000010;

    private readonly object sync = new();
    private readonly HookProc hookProc;
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? hookThread;
    private uint hookThreadId;
    private IntPtr hookHandle;
    private Exception? startException;
    private bool started;
    private bool disposed;

    public WindowsGlobalKeyboardCapture()
    {
        hookProc = HandleHook;
    }

    public event Action<KeyboardPttKey, bool>? KeyChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows global keyboard hook is unavailable here.");

        lock (sync)
        {
            if (started)
                return;

            startException = null;
            hookThread = new Thread(RunHookLoop)
            {
                IsBackground = true,
                Name = "DVM Console global PTT"
            };
            hookThread.Start();
        }

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The Windows global PTT hook did not start in time.");
        if (startException is not null)
            ExceptionDispatchInfo.Capture(startException).Throw();

        started = true;
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (sync)
        {
            if (!started && hookThread is null)
                return;
            thread = hookThread;
            threadId = hookThreadId;
            started = false;
        }

        if (thread is not null && threadId != 0)
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread?.Join(TimeSpan.FromSeconds(2));
        lock (sync)
        {
            if (ReferenceEquals(hookThread, thread))
                hookThread = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        try
        {
            Stop();
        }
        finally
        {
            ready.Dispose();
            disposed = true;
        }
    }

    private void RunHookLoop()
    {
        hookThreadId = GetCurrentThreadId();
        try
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            hookHandle = SetWindowsHookEx(
                WhKeyboardLl,
                hookProc,
                GetModuleHandle(null),
                0);
            if (hookHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed.");

            ready.Set();
            int result;
            while ((result = GetMessage(out _, IntPtr.Zero, 0, 0)) > 0)
            {
                // The low-level hook callback is invoked by the thread's
                // message pump.  There is no application window to dispatch.
            }

            if (result < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
        }
        catch (Exception exception)
        {
            startException = exception;
            ready.Set();
        }
        finally
        {
            if (hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr HandleHook(int code, IntPtr wParam, IntPtr lParam)
    {
        uint message = unchecked((uint)wParam.ToInt64());
        if (code >= 0 && message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
        {
            KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if ((data.Flags & LlkhfInjected) == 0 &&
                KeyboardPttKeyMapping.TryFromWindowsVirtualKey(data.VirtualKey, out KeyboardPttKey key))
            {
                bool isDown = message is WmKeyDown or WmSysKeyDown;
                KeyChanged?.Invoke(key, isDown);
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        HookProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out Message message,
        IntPtr windowHandle,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out Message message,
        IntPtr windowHandle,
        uint minimumMessage,
        uint maximumMessage);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr WindowHandle;
        public uint MessageId;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}

internal sealed class MacGlobalKeyboardCapture : IGlobalKeyboardCapture
{
    private const uint KeyDownEvent = 10;
    private const uint KeyUpEvent = 11;
    private const uint TapDisabledByTimeout = 0xFFFFFFFE;
    private const uint TapDisabledByUserInput = 0xFFFFFFFF;
    private const uint SessionEventTap = 1;
    private const uint HeadInsertEventTap = 0;
    private const uint ListenOnlyEventTap = 1;
    private const uint KeyboardEventKeyCode = 9;
    private const uint Utf8Encoding = 0x08000100;

    private readonly object sync = new();
    private readonly EventTapCallback callback;
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? eventThread;
    private IntPtr runLoop;
    private IntPtr eventTap;
    private IntPtr runLoopSource;
    private Exception? startException;
    private bool started;
    private bool disposed;

    public MacGlobalKeyboardCapture()
    {
        callback = HandleEvent;
    }

    public event Action<KeyboardPttKey, bool>? KeyChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The macOS global keyboard event tap is unavailable here.");

        lock (sync)
        {
            if (started)
                return;
            startException = null;
            eventThread = new Thread(RunEventLoop)
            {
                IsBackground = true,
                Name = "DVM Console global PTT"
            };
            eventThread.Start();
        }

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The macOS global PTT event tap did not start in time.");
        if (startException is not null)
            ExceptionDispatchInfo.Capture(startException).Throw();

        started = true;
    }

    public void Stop()
    {
        Thread? thread;
        IntPtr loop;
        lock (sync)
        {
            if (!started && eventThread is null)
                return;
            thread = eventThread;
            loop = runLoop;
            started = false;
        }

        if (loop != IntPtr.Zero)
            CFRunLoopStop(loop);
        thread?.Join(TimeSpan.FromSeconds(2));
        lock (sync)
        {
            if (ReferenceEquals(eventThread, thread))
                eventThread = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        try
        {
            Stop();
        }
        finally
        {
            ready.Dispose();
            disposed = true;
        }
    }

    private void RunEventLoop()
    {
        IntPtr mode = IntPtr.Zero;
        try
        {
            runLoop = CFRunLoopGetCurrent();
            ulong eventMask = (1UL << (int)KeyDownEvent) | (1UL << (int)KeyUpEvent);
            eventTap = CGEventTapCreate(
                IntPtr.Zero,
                SessionEventTap,
                ListenOnlyEventTap,
                eventMask,
                callback,
                IntPtr.Zero);
            if (eventTap == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException(
                    "macOS did not grant global keyboard access. Enable DVM Console under System Settings > Privacy & Security > Accessibility or Input Monitoring.");
            }

            runLoopSource = CFMachPortCreateRunLoopSource(IntPtr.Zero, eventTap, IntPtr.Zero);
            if (runLoopSource == IntPtr.Zero)
                throw new InvalidOperationException("CFMachPortCreateRunLoopSource failed for the global PTT event tap.");

            mode = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", Utf8Encoding);
            if (mode == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the macOS event-loop mode.");

            CFRunLoopAddSource(runLoop, runLoopSource, mode);
            ready.Set();
            CFRunLoopRun();
        }
        catch (Exception exception)
        {
            startException = exception;
            ready.Set();
        }
        finally
        {
            if (mode != IntPtr.Zero)
                CFRelease(mode);
            if (runLoopSource != IntPtr.Zero)
            {
                CFRelease(runLoopSource);
                runLoopSource = IntPtr.Zero;
            }
            if (eventTap != IntPtr.Zero)
            {
                CFRelease(eventTap);
                eventTap = IntPtr.Zero;
            }
            runLoop = IntPtr.Zero;
        }
    }

    private IntPtr HandleEvent(
        IntPtr proxy,
        uint eventType,
        IntPtr eventHandle,
        IntPtr userInfo)
    {
        if (eventType is TapDisabledByTimeout or TapDisabledByUserInput)
        {
            if (proxy != IntPtr.Zero)
                CGEventTapEnable(proxy, true);
            return eventHandle;
        }

        if (eventType is KeyDownEvent or KeyUpEvent &&
            KeyboardPttKeyMapping.TryFromMacKeyCode(
                CGEventGetIntegerValueField(eventHandle, KeyboardEventKeyCode),
                out KeyboardPttKey key))
        {
            KeyChanged?.Invoke(key, eventType == KeyDownEvent);
        }

        return eventHandle;
    }

    private delegate IntPtr EventTapCallback(
        IntPtr proxy,
        uint eventType,
        IntPtr eventHandle,
        IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventTapCreate(
        IntPtr tap,
        uint place,
        uint options,
        ulong eventsOfInterest,
        EventTapCallback callback,
        IntPtr userInfo);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern long CGEventGetIntegerValueField(IntPtr eventHandle, uint field);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFMachPortCreateRunLoopSource(
        IntPtr allocator,
        IntPtr port,
        IntPtr order);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopAddSource(
        IntPtr runLoop,
        IntPtr source,
        IntPtr mode);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopRun();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        string value,
        uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}

internal static class KeyboardPttKeyMapping
{
    public static bool TryFromWindowsVirtualKey(uint virtualKey, out KeyboardPttKey key)
    {
        key = virtualKey switch
        {
            0x20 => KeyboardPttKey.Space,
            0x70 => KeyboardPttKey.F1,
            0x71 => KeyboardPttKey.F2,
            0x72 => KeyboardPttKey.F3,
            0x73 => KeyboardPttKey.F4,
            0x74 => KeyboardPttKey.F5,
            0x75 => KeyboardPttKey.F6,
            0x76 => KeyboardPttKey.F7,
            0x77 => KeyboardPttKey.F8,
            0x78 => KeyboardPttKey.F9,
            0x79 => KeyboardPttKey.F10,
            0x7A => KeyboardPttKey.F11,
            0x7B => KeyboardPttKey.F12,
            0x7C => KeyboardPttKey.F13,
            0x7D => KeyboardPttKey.F14,
            0x7E => KeyboardPttKey.F15,
            0x7F => KeyboardPttKey.F16,
            0x80 => KeyboardPttKey.F17,
            0x81 => KeyboardPttKey.F18,
            0x82 => KeyboardPttKey.F19,
            _ => default
        };
        return virtualKey is >= 0x70 and <= 0x82 || virtualKey == 0x20;
    }

    public static bool TryFromMacKeyCode(long keyCode, out KeyboardPttKey key)
    {
        key = keyCode switch
        {
            49 => KeyboardPttKey.Space,
            122 => KeyboardPttKey.F1,
            120 => KeyboardPttKey.F2,
            99 => KeyboardPttKey.F3,
            118 => KeyboardPttKey.F4,
            96 => KeyboardPttKey.F5,
            97 => KeyboardPttKey.F6,
            98 => KeyboardPttKey.F7,
            100 => KeyboardPttKey.F8,
            101 => KeyboardPttKey.F9,
            109 => KeyboardPttKey.F10,
            103 => KeyboardPttKey.F11,
            111 => KeyboardPttKey.F12,
            105 => KeyboardPttKey.F13,
            107 => KeyboardPttKey.F14,
            113 => KeyboardPttKey.F15,
            106 => KeyboardPttKey.F16,
            64 => KeyboardPttKey.F17,
            79 => KeyboardPttKey.F18,
            80 => KeyboardPttKey.F19,
            _ => default
        };
        return keyCode is 49 or 122 or 120 or 99 or 118 or 96 or 97 or 98 or 100 or 101 or 109 or 103 or 111 or 105 or 107 or 113 or 106 or 64 or 79 or 80;
    }
}
