using DvmConsole.Application;
using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal enum KeyboardPermissionState
{
    Granted,
    Requested,
    Unavailable
}

internal interface IDesktopPrivacyPermissionService : IMicrophonePermissionService
{
    bool IsMacOsPermissionRequestAvailable { get; }
    KeyboardPermissionState RequestKeyboardAccess();
}

// Native privacy APIs stay at the desktop composition edge. In particular,
// Windows target builds do not reference the macOS audio assembly at all.
internal sealed class DesktopPrivacyPermissionService : IDesktopPrivacyPermissionService
{
    public static DesktopPrivacyPermissionService Instance { get; } = new();

    private DesktopPrivacyPermissionService()
    {
    }

    public bool IsMacOsPermissionRequestAvailable => OperatingSystem.IsMacOS();

    public KeyboardPermissionState RequestKeyboardAccess()
    {
#if !DVMCONSOLE_WINDOWS
        if (OperatingSystem.IsMacOS())
        {
            return MacOsPrivacyPermissionRequester.RequestKeyboardAccess() switch
            {
                MacOsPermissionRequestResult.Granted => KeyboardPermissionState.Granted,
                MacOsPermissionRequestResult.Requested => KeyboardPermissionState.Requested,
                _ => KeyboardPermissionState.Unavailable
            };
        }
#endif
        return KeyboardPermissionState.Unavailable;
    }

    public ValueTask<MicrophonePermissionState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            IsMacOsPermissionRequestAvailable
                ? MicrophonePermissionState.Unknown
                : MicrophonePermissionState.Unavailable);
    }

    public ValueTask<MicrophonePermissionState> RequestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if !DVMCONSOLE_WINDOWS
        if (OperatingSystem.IsMacOS())
        {
            MicrophonePermissionState state = MacOsPrivacyPermissionRequester.RequestMicrophoneAccess() switch
            {
                MacOsPermissionRequestResult.Granted => MicrophonePermissionState.Granted,
                MacOsPermissionRequestResult.Requested => MicrophonePermissionState.Requested,
                MacOsPermissionRequestResult.Denied => MicrophonePermissionState.Denied,
                MacOsPermissionRequestResult.Restricted => MicrophonePermissionState.Restricted,
                _ => MicrophonePermissionState.Unavailable
            };
            return ValueTask.FromResult(state);
        }
#endif
        return ValueTask.FromResult(MicrophonePermissionState.Unavailable);
    }
}
