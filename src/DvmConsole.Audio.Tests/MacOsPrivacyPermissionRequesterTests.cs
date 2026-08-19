using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class MacOsPrivacyPermissionRequesterTests
{
    [Fact]
    public void KeyboardRequestIsPlatformGatedWithoutCallingNativeApis()
    {
        bool invoked = false;

        MacOsPermissionRequestResult result = MacOsPrivacyPermissionRequester.RequestKeyboardAccess(
            isMacOs: false,
            preflight: () => invoked = true,
            request: () => invoked = true);

        Assert.Equal(MacOsPermissionRequestResult.Unavailable, result);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(true, false, MacOsPermissionRequestResult.Granted)]
    [InlineData(false, true, MacOsPermissionRequestResult.Granted)]
    [InlineData(false, false, MacOsPermissionRequestResult.Requested)]
    public void KeyboardRequestReportsNativePermissionState(
        bool preflightGranted,
        bool requestGranted,
        MacOsPermissionRequestResult expected)
    {
        MacOsPermissionRequestResult result = MacOsPrivacyPermissionRequester.RequestKeyboardAccess(
            isMacOs: true,
            preflight: () => preflightGranted,
            request: () => requestGranted);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, MacOsPermissionRequestResult.Unavailable)]
    [InlineData(1, MacOsPermissionRequestResult.Granted)]
    [InlineData(2, MacOsPermissionRequestResult.Requested)]
    [InlineData(3, MacOsPermissionRequestResult.Denied)]
    [InlineData(4, MacOsPermissionRequestResult.Restricted)]
    [InlineData(99, MacOsPermissionRequestResult.Unavailable)]
    public void MicrophoneRequestNormalizesNativeResults(
        int nativeResult,
        MacOsPermissionRequestResult expected)
        => Assert.Equal(expected, MacOsPrivacyPermissionRequester.NormalizeNativeResult(nativeResult));
}
