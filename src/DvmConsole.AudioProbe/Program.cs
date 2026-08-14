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
            using var backend = new MacCoreAudioBackend();
            if (args.FirstOrDefault() is "--stream-test")
                return await RunStreamTestAsync(backend, args.ElementAtOrDefault(1)).ConfigureAwait(false);

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

    private static async Task<int> RunStreamTestAsync(MacCoreAudioBackend backend, string? durationArgument)
    {
        if (!int.TryParse(durationArgument ?? "2", out int seconds) || seconds is < 1 or > 10)
        {
            Console.Error.WriteLine("Stream-test duration must be between 1 and 10 seconds.");
            return 2;
        }

        AudioDeviceInfo input = backend.EnumerateDevices(AudioDirection.Input).First(device => device.IsDefault);
        AudioDeviceInfo output = backend.EnumerateDevices(AudioDirection.Output).First(device => device.IsDefault);
        int capturedSamples = 0;

        await using IAudioCapture capture = backend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit);
        await using IAudioPlayback playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        capture.SamplesAvailable += (_, eventArgs) => capturedSamples += eventArgs.Samples.Length;

        await capture.StartAsync().ConfigureAwait(false);
        await playback.WriteAsync(new short[PcmAudioFormat.Voice8KhzMono16Bit.SampleRate * seconds]).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        await capture.StopAsync().ConfigureAwait(false);

        Console.WriteLine($"Audio stream test completed; captured {capturedSamples} PCM samples.");
        return 0;
    }
}
