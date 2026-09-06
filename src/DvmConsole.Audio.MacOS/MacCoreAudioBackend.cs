// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

// macOS CoreAudio backend. The native shim is loaded explicitly so the rest of
// the application remains independent of CoreAudio and Windows audio APIs.
public sealed class MacCoreAudioBackend :
    IAudioBackend,
    IDefaultAudioDeviceIdentityProvider
{
    private readonly NativeCoreAudioApi api;

    public MacCoreAudioBackend(
        string? libraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacCoreAudioBackend requires macOS.");
        if (processingMode == AudioProcessingMode.WindowsCommunications)
            throw new PlatformNotSupportedException("Windows communications processing requires a Windows audio backend.");
        if (processingMode != AudioProcessingMode.DvmConsole)
            throw new ArgumentOutOfRangeException(nameof(processingMode));
        api = NativeCoreAudioApi.Load(libraryPath);
    }

    public string Name => "macOS CoreAudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
    {
        const int maximumAttempts = 8;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            int input = direction == AudioDirection.Input ? 1 : 0;
            int result = api.GetDeviceCount(input, out int count);
            EnsureSuccess(result, "enumerate audio devices");

            var devices = new List<AudioDeviceInfo>(count);
            bool changedDuringEnumeration = false;
            for (int index = 0; index < count; index++)
            {
                byte[] name = new byte[256];
                result = api.GetDevice(input, index, out ulong deviceId, name, name.Length, out int isDefault);
                if (result == -4)
                {
                    changedDuringEnumeration = true;
                    break;
                }
                EnsureSuccess(result, "read audio device");
                string deviceName = System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(deviceName))
                    deviceName = $"Audio device {deviceId}";
                int bluetooth = api.IsBluetoothDevice(deviceId);
                devices.Add(new AudioDeviceInfo(
                    deviceId.ToString(),
                    deviceName,
                    direction,
                    isDefault != 0,
                    bluetooth < 0 ? null : bluetooth != 0));
            }

            if (!changedDuringEnumeration)
                return devices;
            if (attempt + 1 < maximumAttempts)
                Thread.Sleep(40);
        }

        throw new InvalidOperationException("Unable to read the audio device list because CoreAudio is changing routes. Try again after the microphone mode finishes changing.");
    }

    public string? GetDefaultDeviceIdentity(AudioDirection direction)
        => EnumerateDevices(direction).FirstOrDefault(device => device.IsDefault)?.Id;

    public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong inputDeviceId = ParseDeviceId(device);
        return new MacCoreAudioCapture(api, inputDeviceId, format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong outputDeviceId = ParseDeviceId(device);
        return new MacCoreAudioPlayback(api, outputDeviceId, format);
    }

    public void Dispose() => api.Dispose();

    private static ulong ParseDeviceId(AudioDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Id is null || !ulong.TryParse(device.Id, out ulong deviceId))
            throw new ArgumentException("The CoreAudio device ID is invalid.", nameof(device));
        return deviceId;
    }

    internal static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"Unable to {operation}; CoreAudio status {result}.");
    }

    internal static void EnsureNonNegative(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException($"Unable to {operation}; CoreAudio status {result}.");
    }

    internal static void ValidateVoiceFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels != 1 || format.BitsPerSample != 16)
            throw new NotSupportedException("The macOS voice backend currently supports mono 16-bit PCM only.");
    }

    internal static void ValidatePlaybackFormat(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Channels is not (1 or 2) || format.BitsPerSample != 16)
            throw new NotSupportedException("The macOS audio backend supports mono or stereo 16-bit playback.");
    }

    internal static int ConvertQueueDepthToRequestedRate(
        uint nativeSamples,
        int nativeSampleRate,
        int requestedSampleRate)
    {
        if (nativeSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativeSampleRate));
        if (requestedSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSampleRate));

        long scaled = checked((long)nativeSamples * requestedSampleRate);
        return checked((int)((scaled + nativeSampleRate - 1) / nativeSampleRate));
    }

}
