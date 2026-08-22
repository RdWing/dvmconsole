namespace DvmConsole.Audio;

// macOS CoreAudio backend. The native shim is loaded explicitly so the rest of
// the application remains independent of CoreAudio and Windows audio APIs.
public sealed class MacCoreAudioBackend :
    IAudioBackend,
    IDefaultAudioDeviceIdentityProvider,
    IHighQualityBluetoothAudioStatus
{
    private readonly NativeCoreAudioApi api;
    private readonly AudioProcessingMode processingMode;
    private readonly string configuredInputDeviceId;
    private readonly string configuredOutputDeviceId;
    private readonly bool highQualityBluetoothAudio;

    public MacCoreAudioBackend(
        string? libraryPath = null,
        AudioProcessingMode processingMode = AudioProcessingMode.DvmConsole,
        string? inputDeviceId = null,
        string? outputDeviceId = null,
        bool highQualityBluetoothAudio = false)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacCoreAudioBackend requires macOS.");

        this.processingMode = processingMode;
        configuredInputDeviceId = NormalizeConfiguredDeviceId(inputDeviceId);
        configuredOutputDeviceId = NormalizeConfiguredDeviceId(outputDeviceId);
        this.highQualityBluetoothAudio = highQualityBluetoothAudio;
        api = NativeCoreAudioApi.Load(libraryPath);
    }

    public string Name => "macOS CoreAudio";
    public HighQualityBluetoothAudioStatus HighQualityBluetoothStatus
        => (HighQualityBluetoothAudioStatus)api.GetHighQualityBluetoothStatus();

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
        ulong outputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId);
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing)
        {
            EnsureVoiceProcessingPairSupported(inputDeviceId, outputDeviceId);
            return new MacVoiceProcessingCapture(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    highQualityBluetoothAudio,
                    format,
                    VoiceEndpoint.Capture),
                format);
        }
        return new MacCoreAudioCapture(
            api,
            inputDeviceId,
            outputDeviceId,
            highQualityBluetoothAudio,
            format);
    }

    public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
    {
        ulong outputDeviceId = ParseDeviceId(device);
        if (processingMode == AudioProcessingMode.AppleVoiceProcessing &&
            outputDeviceId == ResolveConfiguredDeviceId(AudioDirection.Output, configuredOutputDeviceId))
        {
            ulong inputDeviceId = ResolveConfiguredDeviceId(AudioDirection.Input, configuredInputDeviceId);
            EnsureVoiceProcessingPairSupported(inputDeviceId, outputDeviceId);
            return new MacVoiceProcessingPlayback(
                VoiceProcessingSessionRegistry.Acquire(
                    api.LibraryPath,
                    inputDeviceId,
                    outputDeviceId,
                    highQualityBluetoothAudio,
                    format,
                    VoiceEndpoint.Playback),
                format);
        }
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

    private ulong ResolveConfiguredDeviceId(AudioDirection direction, string configuredId)
    {
        IReadOnlyList<AudioDeviceInfo> devices = EnumerateDevices(direction);
        AudioDeviceInfo device = devices.FirstOrDefault(candidate =>
                !configuredId.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                candidate.Id.Equals(configuredId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(candidate => candidate.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException($"No {direction.ToString().ToLowerInvariant()} audio device is available.");
        return ParseDeviceId(device);
    }

    private void EnsureVoiceProcessingPairSupported(ulong inputDeviceId, ulong outputDeviceId)
    {
        if (inputDeviceId == outputDeviceId)
            return;

        bool inputIsDefault = EnumerateDevices(AudioDirection.Input)
            .Any(device => device.IsDefault && ParseDeviceId(device) == inputDeviceId);
        bool outputIsDefault = EnumerateDevices(AudioDirection.Output)
            .Any(device => device.IsDefault && ParseDeviceId(device) == outputDeviceId);
        if (inputIsDefault && outputIsDefault)
            return;

        throw new NotSupportedException(
            "Apple voice processing on macOS requires the system-default input/output pair " +
            "or one duplex device that provides both input and output. Choose a compatible paired route " +
            "or use DVM Console processing for separate devices.");
    }

    private static string NormalizeConfiguredDeviceId(string? deviceId)
        => string.IsNullOrWhiteSpace(deviceId) ? "default" : deviceId.Trim();

    internal static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
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
