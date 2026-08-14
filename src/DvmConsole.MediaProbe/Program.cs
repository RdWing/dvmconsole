using System.Threading.Channels;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.MediaProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Usage: DvmConsole.MediaProbe <codeplug.yml> [duration-seconds]");
            Console.WriteLine("The probe selects the first DMR/P25 channel and uses the default output device.");
            return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 2;
        }

        if (!int.TryParse(args.ElementAtOrDefault(1) ?? "10", out int durationSeconds) || durationSeconds is < 1 or > 300)
        {
            Console.Error.WriteLine("Duration must be an integer between 1 and 300 seconds.");
            return 2;
        }

        ConsoleConfiguration configuration = ConfigurationLoader.Load(args[0]);
        IReadOnlyList<string> validationErrors = ConfigurationLoader.Validate(configuration);
        if (validationErrors.Count > 0)
        {
            foreach (string error in validationErrors)
                Console.Error.WriteLine(error);
            return 1;
        }

        ChannelConfiguration? channel = configuration.Zones
            .SelectMany(zone => zone.Channels)
            .FirstOrDefault(candidate => candidate.Mode.Equals("dmr", StringComparison.OrdinalIgnoreCase) ||
                                         candidate.Mode.Equals("p25", StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            Console.Error.WriteLine("The codeplug contains no DMR or P25 channel for media probing.");
            return 1;
        }

        SystemConfiguration? system = configuration.Systems.FirstOrDefault(candidate =>
            candidate.Name.Equals(channel.System, StringComparison.OrdinalIgnoreCase));
        if (system is null || !uint.TryParse(channel.Tgid, out uint destinationId))
        {
            Console.Error.WriteLine("The selected media channel does not resolve to a valid system and destination ID.");
            return 1;
        }

        try
        {
            return await RunProbeAsync(system, channel, destinationId, durationSeconds).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Media probe failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunProbeAsync(
        SystemConfiguration system,
        ChannelConfiguration channel,
        uint destinationId,
        int durationSeconds)
    {
        using IAudioBackend audio = AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));
        AudioDeviceInfo output = audio.EnumerateDevices(AudioDirection.Output).FirstOrDefault(device => device.IsDefault)
            ?? audio.EnumerateDevices(AudioDirection.Output).FirstOrDefault()
            ?? throw new InvalidOperationException("No audio output device is available.");
        IAudioPlayback playback = audio.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        using var vocoder = new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY"));

        IAsyncDisposable mediaSession;
        Func<FneTrafficFrame, CancellationToken, ValueTask<int>> processTraffic;
        byte selectedSlot = (byte)Math.Clamp(channel.Slot - 1, 0, 1);
        if (channel.Mode.Equals("dmr", StringComparison.OrdinalIgnoreCase))
        {
            var router = new DmrRxAudioRouter(
                new DmrTrafficSelector(destinationId, selectedSlot),
                vocoder.CreateSession(VocoderMode.DmrAmbe),
                playback);
            mediaSession = router;
            processTraffic = router.ProcessAsync;
        }
        else
        {
            var session = new P25RxAudioSession(
                new P25TrafficSelector(destinationId),
                vocoder.CreateSession(VocoderMode.P25Imbe),
                playback);
            mediaSession = session;
            processTraffic = session.ProcessAsync;
        }

        await using (mediaSession.ConfigureAwait(false))
        {
            var connection = new FneConnection(FneConnectionOptions.FromConfiguration(system));
            var traffic = Channel.CreateUnbounded<FneTrafficFrame>();
            int trafficReceived = 0;
            connection.StatusChanged += HandleStatus;
            connection.TrafficReceived += (_, frame) =>
            {
                Interlocked.Increment(ref trafficReceived);
                traffic.Writer.TryWrite(frame);
            };
            Task worker = ConsumeTrafficAsync(traffic.Reader, processTraffic);

            try
            {
                Console.WriteLine($"Selected {channel.Mode.ToUpperInvariant()} channel '{channel.Name}' on system '{system.Name}'.");
                Console.WriteLine($"Output device: {output.Name}. Running for {durationSeconds} seconds.");
                await connection.StartAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(durationSeconds)).ConfigureAwait(false);
            }
            finally
            {
                await connection.StopAsync().ConfigureAwait(false);
                traffic.Writer.TryComplete();
                await worker.ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                connection.StatusChanged -= HandleStatus;
            }

            int decodedFrames = mediaSession switch
            {
                DmrRxAudioRouter dmr => dmr.FramesDecoded,
                P25RxAudioSession p25 => p25.FramesDecoded,
                _ => 0
            };
            Console.WriteLine($"Media probe completed cleanly; received {trafficReceived} FNE frame(s), decoded {decodedFrames} vocoder frame(s).");
            return 0;
        }
    }

    private static async Task ConsumeTrafficAsync(
        ChannelReader<FneTrafficFrame> reader,
        Func<FneTrafficFrame, CancellationToken, ValueTask<int>> processTraffic)
    {
        await foreach (FneTrafficFrame frame in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await processTraffic(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Media frame failed: {exception.Message}");
            }
        }
    }

    private static void HandleStatus(object? sender, FneConnectionStatus status)
    {
        Console.WriteLine($"[{status.Name}] {status.State}: {status.Message}");
    }
}
