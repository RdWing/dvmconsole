using NAudio.CoreAudioApi;
using System.Runtime.Versioning;

namespace DvmConsole.Audio;

// Windows platform adapter. Endpoint discovery and stream implementations are
// kept separate so routing policy remains independent of the WASAPI mechanics.
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioBackend : IAudioBackend, IDefaultAudioDeviceIdentityProvider
{
    private readonly WindowsWasapiDeviceCatalog devices = new();
    private readonly AudioProcessingMode processingMode;

    public WindowsAudioBackend(AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsAudioBackend requires Windows.");
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
            throw new PlatformNotSupportedException("Apple voice processing requires an Apple audio backend.");
        if (processingMode is not AudioProcessingMode.DvmConsole and not AudioProcessingMode.WindowsCommunications)
            throw new ArgumentOutOfRangeException(nameof(processingMode));

        this.processingMode = processingMode;
    }

    public string Name => "Windows WASAPI";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
        => devices.EnumerateDevices(direction);

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
        => devices.GetDefaultDeviceIdentity(direction);

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateFormat(format, allowStereo: false);
        MMDevice endpoint = devices.OpenDevice(device, AudioDirection.Input);
        try
        {
            return new WindowsWasapiCapture(
                endpoint,
                format,
                useCommunicationsMode: processingMode == AudioProcessingMode.WindowsCommunications);
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateFormat(format, allowStereo: true);
        MMDevice endpoint = devices.OpenDevice(device, AudioDirection.Output);
        try
        {
            return new WindowsWasapiPlayback(endpoint, format);
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
    }

    private static void ValidateFormat(PcmAudioFormat format, bool allowStereo)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels < 1 || format.Channels > (allowStereo ? 2 : 1) || format.BitsPerSample != 16)
            throw new NotSupportedException("The Windows audio backend supports mono capture and mono or stereo 16-bit playback.");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsWasapiDeviceCatalog
{
    internal const string DefaultDeviceId = "default";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        DataFlow dataFlow = ToDataFlow(direction);
        using var enumerator = new MMDeviceEnumerator();
        string? defaultIdentity = GetDefaultDeviceIdentity(enumerator, dataFlow);
        var endpointDescriptors = new List<WindowsWasapiEndpointDescriptor>();
        using MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        foreach (MMDevice endpoint in endpoints)
        {
            endpointDescriptors.Add(new WindowsWasapiEndpointDescriptor(endpoint.ID, endpoint.FriendlyName));
        }

        return BuildDeviceList(direction, endpointDescriptors, defaultIdentity);
    }

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
    {
        using var enumerator = new MMDeviceEnumerator();
        return GetDefaultDeviceIdentity(enumerator, ToDataFlow(direction));
    }

    public MMDevice OpenDevice(AudioDeviceInfo device, AudioDirection direction)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Direction != direction)
            throw new ArgumentException("The selected audio endpoint has the wrong direction.", nameof(device));

        WindowsWasapiEndpointSelection selection = CreateSelection(device, direction);
        using var enumerator = new MMDeviceEnumerator();
        MMDevice endpoint = selection.UseDefault
            ? enumerator.GetDefaultAudioEndpoint(selection.DataFlow, selection.Role)
            : enumerator.GetDevice(selection.EndpointId);

        if (endpoint.State != DeviceState.Active || endpoint.DataFlow != selection.DataFlow)
        {
            endpoint.Dispose();
            throw new InvalidOperationException("The selected audio endpoint is not active or has the wrong direction.");
        }

        return endpoint;
    }

    internal static IReadOnlyList<AudioDeviceInfo> BuildDeviceList(
        AudioDirection direction,
        IEnumerable<WindowsWasapiEndpointDescriptor> endpoints,
        string? defaultIdentity)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var devices = new List<AudioDeviceInfo>
        {
            new(
                DefaultDeviceId,
                direction == AudioDirection.Input ? "Windows default input" : "Windows default output",
                direction,
                true,
                false)
        };
        devices.AddRange(endpoints.Select(endpoint => new AudioDeviceInfo(
            endpoint.Id,
            endpoint.FriendlyName,
            direction,
            string.Equals(endpoint.Id, defaultIdentity, StringComparison.Ordinal),
            false)));
        return devices;
    }

    internal static WindowsWasapiEndpointSelection CreateSelection(
        AudioDeviceInfo device,
        AudioDirection direction)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Direction != direction)
            throw new ArgumentException("The selected audio endpoint has the wrong direction.", nameof(device));

        return new WindowsWasapiEndpointSelection(
            string.Equals(device.Id, DefaultDeviceId, StringComparison.OrdinalIgnoreCase),
            device.Id,
            ToDataFlow(direction),
            Role.Multimedia);
    }

    private static string? GetDefaultDeviceIdentity(MMDeviceEnumerator enumerator, DataFlow dataFlow)
    {
        if (!enumerator.HasDefaultAudioEndpoint(dataFlow, Role.Multimedia))
            return null;

        using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
        return endpoint.ID;
    }

    private static DataFlow ToDataFlow(AudioDirection direction) => direction switch
    {
        AudioDirection.Input => DataFlow.Capture,
        AudioDirection.Output => DataFlow.Render,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

internal sealed record WindowsWasapiEndpointDescriptor(string Id, string FriendlyName);

internal sealed record WindowsWasapiEndpointSelection(
    bool UseDefault,
    string EndpointId,
    DataFlow DataFlow,
    Role Role);
