using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

public enum MacOsPermissionRequestResult
{
    Unavailable = 0,
    Granted = 1,
    Requested = 2,
    Denied = 3,
    Restricted = 4
}

// Keeps macOS privacy APIs behind a platform boundary so desktop UI code does
// not need to know about CoreGraphics or the native Core Audio shim.
public static class MacOsPrivacyPermissionRequester
{
    private const string CoreGraphicsFramework =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string NativeAudioLibraryName = "libdvmaudio.dylib";

    private static readonly Lazy<RequestMicrophonePermissionDelegate> RequestMicrophonePermission =
        new(LoadMicrophonePermissionRequest, LazyThreadSafetyMode.ExecutionAndPublication);

    public static MacOsPermissionRequestResult RequestKeyboardAccess()
        => RequestKeyboardAccess(
            OperatingSystem.IsMacOS(),
            CGPreflightListenEventAccess,
            CGRequestListenEventAccess);

    internal static MacOsPermissionRequestResult RequestKeyboardAccess(
        bool isMacOs,
        Func<bool> preflight,
        Func<bool> request)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(request);
        if (!isMacOs)
            return MacOsPermissionRequestResult.Unavailable;
        if (preflight())
            return MacOsPermissionRequestResult.Granted;

        return request()
            ? MacOsPermissionRequestResult.Granted
            : MacOsPermissionRequestResult.Requested;
    }

    public static MacOsPermissionRequestResult RequestMicrophoneAccess()
    {
        if (!OperatingSystem.IsMacOS())
            return MacOsPermissionRequestResult.Unavailable;

        return NormalizeNativeResult(RequestMicrophonePermission.Value());
    }

    internal static MacOsPermissionRequestResult NormalizeNativeResult(int result)
        => Enum.IsDefined(typeof(MacOsPermissionRequestResult), result)
            ? (MacOsPermissionRequestResult)result
            : MacOsPermissionRequestResult.Unavailable;

    private static RequestMicrophonePermissionDelegate LoadMicrophonePermissionRequest()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY");
        string libraryPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, NativeAudioLibraryName)
            : configuredPath;
        // The delegate remains valid for the process lifetime, so deliberately
        // retain the native-library reference instead of freeing it here.
        IntPtr handle = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        IntPtr function = NativeLibrary.GetExport(handle, "dvm_audio_request_microphone_permission");
        return Marshal.GetDelegateForFunctionPointer<RequestMicrophonePermissionDelegate>(function);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RequestMicrophonePermissionDelegate();

    [DllImport(CoreGraphicsFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightListenEventAccess();

    [DllImport(CoreGraphicsFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestListenEventAccess();
}
