using DvmConsole.Audio;
using NAudio.CoreAudioApi;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class WindowsWasapiDeviceCatalogTests
{
    [Fact]
    public void DeviceListRetainsStableEndpointIdsAndSyntheticDefault()
    {
        WindowsWasapiEndpointDescriptor[] endpoints =
        [
            new("endpoint-speakers", "Speakers (USB Audio)"),
            new("endpoint-headset", "Headset Earphone")
        ];

        IReadOnlyList<AudioDeviceInfo> devices = WindowsWasapiDeviceCatalog.BuildDeviceList(
            AudioDirection.Output,
            endpoints,
            defaultIdentity: "endpoint-headset");

        Assert.Equal("default", devices[0].Id);
        Assert.True(devices[0].IsDefault);
        Assert.Equal("endpoint-speakers", devices[1].Id);
        Assert.Equal("Speakers (USB Audio)", devices[1].Name);
        Assert.False(devices[1].IsDefault);
        Assert.Equal("endpoint-headset", devices[2].Id);
        Assert.True(devices[2].IsDefault);
        Assert.All(devices, device => Assert.False(device.IsBluetooth));
    }

    [Theory]
    [InlineData(AudioDirection.Input)]
    [InlineData(AudioDirection.Output)]
    public void WindowsEndpointsDoNotUseMacBluetoothProfileTransitionPolicy(
        AudioDirection direction)
    {
        WindowsWasapiEndpointDescriptor[] endpoints =
        [
            new("endpoint-wired", "USB wired headset"),
            new("endpoint-bluetooth", "Bluetooth hands-free headset")
        ];

        IReadOnlyList<AudioDeviceInfo> devices = WindowsWasapiDeviceCatalog.BuildDeviceList(
            direction,
            endpoints,
            defaultIdentity: "endpoint-bluetooth");

        Assert.All(devices, device => Assert.False(device.IsBluetooth));
    }

    [Fact]
    public void SyntheticDefaultResolvesWithMultimediaRole()
    {
        var device = new AudioDeviceInfo(
            "default",
            "System default",
            AudioDirection.Input,
            true);

        WindowsWasapiEndpointSelection selection = WindowsWasapiDeviceCatalog.CreateSelection(
            device,
            AudioDirection.Input);

        Assert.True(selection.UseDefault);
        Assert.Equal(DataFlow.Capture, selection.DataFlow);
        Assert.Equal(Role.Multimedia, selection.Role);
    }

    [Fact]
    public void FixedEndpointResolvesByItsStableId()
    {
        var device = new AudioDeviceInfo(
            "{0.0.1.00000000}.fixed-endpoint",
            "Fixed microphone",
            AudioDirection.Input,
            false);

        WindowsWasapiEndpointSelection selection = WindowsWasapiDeviceCatalog.CreateSelection(
            device,
            AudioDirection.Input);

        Assert.False(selection.UseDefault);
        Assert.Equal(device.Id, selection.EndpointId);
        Assert.Equal(Role.Multimedia, selection.Role);
    }
}
