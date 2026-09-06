// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Ptt;

namespace DvmConsole.AudioProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() is "--global-ptt")
                return await RunGlobalPttAsync(
                    args.ElementAtOrDefault(1),
                    args.ElementAtOrDefault(2)).ConfigureAwait(false);

            if (args.FirstOrDefault() is "--monitor-linux-devices")
                return await RunLinuxDeviceMonitorAsync(
                    args.ElementAtOrDefault(1),
                    args.ElementAtOrDefault(2)).ConfigureAwait(false);

            if (OperatingSystem.IsLinux())
            {
                string? libraryPath = args.FirstOrDefault() is "--linux-devices"
                    ? args.ElementAtOrDefault(1)
                    : args.FirstOrDefault() is "--linux-stream-test"
                        ? args.ElementAtOrDefault(2)
                    : null;
                using var linuxBackend = new LinuxPipeWireBackend(libraryPath);
                if (args.FirstOrDefault() is "--linux-stream-test")
                {
                    return await RunStreamTestAsync(
                        linuxBackend,
                        args.ElementAtOrDefault(1)).ConfigureAwait(false);
                }
                PrintDevices(linuxBackend);
                return 0;
            }
            if (!OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine("The audio probe supports macOS and Linux desktop hosts.");
                return 2;
            }

            using var backend = new MacCoreAudioBackend();
            if (args.FirstOrDefault() is "--stream-test")
                return await RunStreamTestAsync(backend, args.ElementAtOrDefault(1)).ConfigureAwait(false);
            if (args.FirstOrDefault() is "--permit-tone")
                return await RunPermitToneAsync(backend, args.ElementAtOrDefault(1)).ConfigureAwait(false);

            PrintDevices(backend);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Audio probe failed: {exception.Message}");
            for (Exception? cause = exception.InnerException; cause is not null; cause = cause.InnerException)
                Console.Error.WriteLine($"Caused by: {cause.GetType().Name}: {cause.Message}");
            return 1;
        }
    }

    private static void PrintDevices(IAudioBackend backend)
    {
        foreach (AudioDirection direction in Enum.GetValues<AudioDirection>())
        {
            Console.WriteLine($"{direction} devices:");
            foreach (AudioDeviceInfo device in backend.EnumerateDevices(direction))
            {
                string transport = device.IsBluetooth switch
                {
                    true => "Bluetooth",
                    false => "non-Bluetooth",
                    null => "transport unknown"
                };
                Console.WriteLine(
                    $"  {(device.IsDefault ? "*" : " ")} {device.Id}: {device.Name} [{transport}]");
            }
        }
    }

    private static async Task<int> RunStreamTestAsync(
        IAudioBackend backend,
        string? durationArgument)
    {
        if (!int.TryParse(durationArgument ?? "2", out int seconds) || seconds is < 1 or > 10)
        {
            Console.Error.WriteLine("Stream-test duration must be between 1 and 10 seconds.");
            return 2;
        }

        AudioDeviceInfo input = backend.EnumerateDevices(AudioDirection.Input)
            .FirstOrDefault(device => device.IsDefault)
            ?? throw new InvalidOperationException("No default input device is available.");
        AudioDeviceInfo output = backend.EnumerateDevices(AudioDirection.Output)
            .FirstOrDefault(device => device.IsDefault)
            ?? throw new InvalidOperationException("No default output device is available.");
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
        await capture.StopAsync().ConfigureAwait(false);

        Console.WriteLine($"Input: {input.Id}: {input.Name}");
        Console.WriteLine($"Output: {output.Id}: {output.Name}");
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

    private static async Task<int> RunGlobalPttAsync(
        string? keyArgument,
        string? durationArgument)
    {
        KeyboardPttKey key = Enum.TryParse(keyArgument, ignoreCase: true, out KeyboardPttKey parsedKey)
            ? parsedKey
            : KeyboardPttKey.Space;
        if (!int.TryParse(durationArgument ?? "2", out int seconds) || seconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(durationArgument), "Duration must be between 1 and 120 seconds.");
        await using var ptt = new GlobalKeyboardPttSource(key);
        ptt.StateChanged += (_, pressed) => Console.WriteLine($"Global PTT {(pressed ? "pressed" : "released")}.");
        await ptt.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"Global PTT capture started for {key}; press the key or wait for the lifecycle check.");
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        await ptt.StopAsync().ConfigureAwait(false);
        Console.WriteLine("Global PTT capture stopped cleanly.");
        return 0;
    }

    private static async Task<int> RunLinuxDeviceMonitorAsync(
        string? libraryPath,
        string? durationArgument)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The PipeWire device monitor requires Linux.");
        if (!int.TryParse(durationArgument ?? "5", out int seconds) || seconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(durationArgument), "Duration must be between 1 and 30 seconds.");

        int changes = 0;
        using var source = new LinuxPipeWireDeviceChangeSource(libraryPath);
        source.Changed += (_, _) =>
        {
            changes++;
            Console.WriteLine($"PipeWire device topology changed ({changes}).");
        };
        source.Start();
        Console.WriteLine("PipeWire device monitor started.");
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        Console.WriteLine($"PipeWire device monitor stopped; observed {changes} change notification(s).");
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
