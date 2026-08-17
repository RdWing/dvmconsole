using DvmConsole.Audio;

namespace DvmConsole.AudioProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("The CoreAudio probe requires macOS.");
            return 2;
        }

        try
        {
            if (args.FirstOrDefault() is "--global-ptt")
                return await RunGlobalPttAsync(args.ElementAtOrDefault(1)).ConfigureAwait(false);

            bool voiceProcessing = args.FirstOrDefault() is "--voice-processing-stream-test";
            string? requestedInputDeviceId = voiceProcessing ? args.ElementAtOrDefault(2) : null;
            string? requestedOutputDeviceId = voiceProcessing ? args.ElementAtOrDefault(3) : null;
            using var backend = new MacCoreAudioBackend(
                processingMode: voiceProcessing
                    ? AudioProcessingMode.AppleVoiceProcessing
                    : AudioProcessingMode.DvmConsole,
                inputDeviceId: requestedInputDeviceId,
                outputDeviceId: requestedOutputDeviceId);
            if (voiceProcessing)
                return await RunStreamTestAsync(
                    backend,
                    args.ElementAtOrDefault(1),
                    allowSystemDefaultsWithoutEnumeration: true,
                    requestedInputDeviceId,
                    requestedOutputDeviceId).ConfigureAwait(false);
            if (args.FirstOrDefault() is "--stream-test")
                return await RunStreamTestAsync(backend, args.ElementAtOrDefault(1)).ConfigureAwait(false);
            if (args.FirstOrDefault() is "--permit-tone")
                return await RunPermitToneAsync(backend, args.ElementAtOrDefault(1)).ConfigureAwait(false);

            foreach (AudioDirection direction in Enum.GetValues<AudioDirection>())
            {
                Console.WriteLine($"{direction} devices:");
                foreach (AudioDeviceInfo device in backend.EnumerateDevices(direction))
                    Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Id}: {device.Name}");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Audio probe failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunStreamTestAsync(
        MacCoreAudioBackend backend,
        string? durationArgument,
        bool allowSystemDefaultsWithoutEnumeration = false,
        string? requestedInputDeviceId = null,
        string? requestedOutputDeviceId = null)
    {
        if (!int.TryParse(durationArgument ?? "2", out int seconds) || seconds is < 1 or > 10)
        {
            Console.Error.WriteLine("Stream-test duration must be between 1 and 10 seconds.");
            return 2;
        }

        AudioDeviceInfo input = backend.EnumerateDevices(AudioDirection.Input).FirstOrDefault(device =>
                !string.IsNullOrWhiteSpace(requestedInputDeviceId) && device.Id == requestedInputDeviceId)
            ?? backend.EnumerateDevices(AudioDirection.Input).FirstOrDefault(device => device.IsDefault)
            ?? (allowSystemDefaultsWithoutEnumeration
                ? new AudioDeviceInfo("default", "System default input", AudioDirection.Input, true)
                : throw new InvalidOperationException("No default input device is available."));
        AudioDeviceInfo output = backend.EnumerateDevices(AudioDirection.Output).FirstOrDefault(device =>
                !string.IsNullOrWhiteSpace(requestedOutputDeviceId) && device.Id == requestedOutputDeviceId)
            ?? backend.EnumerateDevices(AudioDirection.Output).FirstOrDefault(device => device.IsDefault)
            ?? (allowSystemDefaultsWithoutEnumeration
                ? new AudioDeviceInfo("default", "System default output", AudioDirection.Output, true)
                : throw new InvalidOperationException("No default output device is available."));
        int capturedSamples = 0;
        int peakSample = 0;

        await using IAudioCapture capture = backend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit);
        await using IAudioPlayback playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        capture.SamplesAvailable += (_, eventArgs) =>
        {
            capturedSamples += eventArgs.Samples.Length;
            foreach (short sample in eventArgs.Samples.Span)
                peakSample = Math.Max(peakSample, Math.Abs((int)sample));
        };

        await capture.StartAsync().ConfigureAwait(false);
        await playback.WriteAsync(new short[PcmAudioFormat.Voice8KhzMono16Bit.SampleRate * seconds]).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        HighQualityBluetoothAudioStatus bluetoothStatus = backend.HighQualityBluetoothStatus;
        await capture.StopAsync().ConfigureAwait(false);

        Console.WriteLine($"Input: {input.Id}: {input.Name}");
        Console.WriteLine($"Output: {output.Id}: {output.Name}");
        Console.WriteLine($"High-quality Bluetooth: {bluetoothStatus}");
        Console.WriteLine($"Audio stream test completed; captured {capturedSamples} PCM samples; peak level {peakSample}.");
        return 0;
    }

    private static async Task<int> RunPermitToneAsync(MacCoreAudioBackend backend, string? requestedDeviceId)
    {
        AudioDeviceInfo output = ResolveOutputDevice(backend, requestedDeviceId);
        await using IAudioPlayback playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        short[] samples = new PcmToneGenerator().GenerateTone(
            frequency: 1200,
            duration: TimeSpan.FromMilliseconds(120),
            amplitude: 0.40);
        ApplyFade(samples, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate / 100);

        await playback.WriteAsync(samples).ConfigureAwait(false);
        int? queuedSamples = playback.QueuedSamples;
        int? consumedSamples = await playback.DrainAsync().ConfigureAwait(false);
        Console.WriteLine($"Permit tone completed on {output.Id}: {output.Name}; queued {queuedSamples?.ToString() ?? "unknown"} / consumed {consumedSamples?.ToString() ?? "unknown"} samples.");
        return 0;
    }

    private static async Task<int> RunGlobalPttAsync(string? keyArgument)
    {
        KeyboardPttKey key = Enum.TryParse(keyArgument, ignoreCase: true, out KeyboardPttKey parsedKey)
            ? parsedKey
            : KeyboardPttKey.Space;
        await using var ptt = new GlobalKeyboardPttSource(key);
        ptt.StateChanged += (_, pressed) => Console.WriteLine($"Global PTT {(pressed ? "pressed" : "released")}.");
        await ptt.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"Global PTT capture started for {key}; press the key or wait for the lifecycle check.");
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await ptt.StopAsync().ConfigureAwait(false);
        Console.WriteLine("Global PTT capture stopped cleanly.");
        return 0;
    }

    private static AudioDeviceInfo ResolveOutputDevice(MacCoreAudioBackend backend, string? requestedDeviceId)
    {
        IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
        return devices.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(requestedDeviceId) &&
                   device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(device => device.IsDefault)
               ?? devices.FirstOrDefault()
               ?? throw new InvalidOperationException("No audio output device is available for the permit-tone probe.");
    }

    private static void ApplyFade(short[] samples, int fadeSamples)
    {
        int boundedFade = Math.Min(Math.Max(0, fadeSamples), samples.Length / 2);
        for (int index = 0; index < boundedFade; index++)
        {
            double scale = (double)index / boundedFade;
            samples[index] = (short)Math.Round(samples[index] * scale);
            int tail = samples.Length - index - 1;
            samples[tail] = (short)Math.Round(samples[tail] * scale);
        }
    }
}
