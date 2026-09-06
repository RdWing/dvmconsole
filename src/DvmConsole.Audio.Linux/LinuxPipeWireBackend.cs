// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;

namespace DvmConsole.Audio;

// Linux platform adapter. PipeWire owns native route selection and format
// conversion; the managed boundary remains 16-bit interleaved PCM.
public sealed class LinuxPipeWireBackend :
    IAudioBackend,
    IDefaultAudioDeviceIdentityProvider
{
    internal const string DefaultDeviceId = "default";
    private readonly IPipeWireApi api;

    public LinuxPipeWireBackend(string? libraryPath = null)
        : this(LoadForLinux(libraryPath))
    {
    }

    internal LinuxPipeWireBackend(IPipeWireApi api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public string Name => "Linux PipeWire";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        int input = direction == AudioDirection.Input ? 1 : 0;
        EnsureSuccess(api.GetDeviceCount(input, out int count), "enumerate PipeWire audio devices");
        var devices = new List<AudioDeviceInfo>(count);
        byte[] nameBuffer = new byte[256];
        for (int index = 0; index < count; index++)
        {
            EnsureSuccess(
                api.GetDevice(
                    input,
                    index,
                    out ulong deviceId,
                    nameBuffer,
                    nameBuffer.Length,
                    out int isDefault),
                "read a PipeWire audio device");
            int nameLength = Array.IndexOf(nameBuffer, (byte)0);
            if (nameLength < 0)
                nameLength = nameBuffer.Length;
            string displayName = Encoding.UTF8.GetString(nameBuffer, 0, nameLength);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = direction == AudioDirection.Input
                    ? "PipeWire default input"
                    : "PipeWire default output";
            }

            devices.Add(new AudioDeviceInfo(
                FormatDeviceId(deviceId),
                displayName,
                direction,
                isDefault != 0,
                IsBluetooth(api.IsBluetoothDevice(deviceId))));
        }

        return devices;
    }

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
        => EnumerateDevices(direction).FirstOrDefault(device => device.IsDefault)?.Id;

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateDevice(device, AudioDirection.Input);
        ValidateFormat(format, allowStereo: false);
        return new LinuxPipeWireCapture(api, ParseDeviceId(device), format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ValidateDevice(device, AudioDirection.Output);
        ValidateFormat(format, allowStereo: true);
        return new LinuxPipeWirePlayback(api, ParseDeviceId(device), format);
    }

    public void Dispose() => api.Dispose();

    internal static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"Unable to {operation}; PipeWire status {result}.");
    }

    internal static void EnsureNonNegative(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException($"Unable to {operation}; PipeWire status {result}.");
    }

    private static void ValidateFormat(PcmAudioFormat format, bool allowStereo)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels < 1 || format.Channels > (allowStereo ? 2 : 1) || format.BitsPerSample != 16)
        {
            throw new NotSupportedException(
                "The Linux PipeWire backend supports mono capture and mono or stereo 16-bit playback.");
        }
    }

    private static void ValidateDevice(AudioDeviceInfo device, AudioDirection direction)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Direction != direction)
            throw new ArgumentException("The selected audio endpoint has the wrong direction.", nameof(device));
    }

    private static string FormatDeviceId(ulong deviceId)
        => deviceId == 0 ? DefaultDeviceId : deviceId.ToString();

    private static ulong ParseDeviceId(AudioDeviceInfo device)
    {
        if (device.Id.Equals(DefaultDeviceId, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (ulong.TryParse(device.Id, out ulong deviceId))
            return deviceId;
        throw new ArgumentException("The PipeWire device ID is invalid.", nameof(device));
    }

    private static bool? IsBluetooth(int classification) => classification switch
    {
        0 => false,
        1 => true,
        _ => null
    };

    private static IPipeWireApi LoadForLinux(string? libraryPath)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxPipeWireBackend requires Linux.");
        return NativePipeWireApi.Load(libraryPath);
    }
}
