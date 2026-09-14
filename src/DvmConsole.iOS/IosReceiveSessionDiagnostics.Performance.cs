// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Media;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Storage;
using DvmConsole.Vocoder;

namespace DvmConsole.iOS;

/// <summary>Opt-in load measurement using the production runtime, renderer, native audio and codecs.</summary>
internal static partial class IosReceiveSessionDiagnostics
{
    public static async Task<string> RunPerformanceAsync()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string report = Path.Combine(documents, "performance.csv");
        await File.WriteAllTextAsync(report,
            "channels,routes,phase,seconds,allocated_bytes,gc0,gc1,gc2,meter_events,rx_queue_peak,rx_latency_p95_ms,route_recoveries,output_starved_ms,output_pending_starved_ms,capture_dropped_samples,output_callbacks\n");
        (int Channels, int Routes)[] scenarios = [(1, 1), (10, 1), (100, 1), (10, 10)];
        foreach (var scenario in scenarios)
            await MeasureConfigurationAsync(scenario.Channels, scenario.Routes, documents, report);
        return "PASS\nMeasured 1/10/100 same-talkgroup listeners and ten independent talkgroups, idle then paced DMR reception with TAR, native output and live mobile presentation. See performance.csv. Not device capacity qualification.";
    }

    private static byte[][] CreatePerformancePackets(uint destination)
    {
        using var backend = new SoftwareVocoderBackend(linkage: NativeVocoderLinkage.StaticallyLinked);
        using var voice = backend.CreateSession(VocoderMode.DmrAmbe);
        short[] pcm = new short[160];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(3000 * Math.Sin(2 * Math.PI * 440 * i / 8000));
        var packets = new byte[6][];
        for (byte packet = 0; packet < packets.Length; packet++)
        {
            byte[] ambe = new byte[27];
            for (int frame = 0; frame < 3; frame++)
                if (voice.Encode(pcm, ambe.AsSpan(frame * 9, 9)) != 9)
                    throw new InvalidOperationException("Performance voice fixture encoding failed.");
            packets[packet] = DmrVoicePacketCodec.CreateVoicePacket(2, destination, 0, true, 0, packet, ambe);
        }
        return packets;
    }

    private static async Task MeasureConfigurationAsync(int count, int routes, string documents, string report)
    {
        await File.WriteAllTextAsync(Path.Combine(documents, "performance-phase.txt"), $"{count},{routes},startup");
        byte[][][] packets = Enumerable.Range(0, routes).Select(index => CreatePerformancePackets((uint)(100 + index))).ToArray();
        string root = Path.Combine(Path.GetTempPath(), "neo-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var master = new FneLoopbackMaster();
            using var audio = new IosAudioSessionOwner();
            await using var recordings = new OpusRecordingStore(Path.Combine(root, "Recordings"), null, 0);
            using var keys = new P25KeyRing();
            var endpoint = master.CreateOptions("Performance");
            var configuration = new ConsoleConfiguration
            {
                Systems = [new DvmConsole.Core.Configuration.SystemConfiguration { Name = "Performance", Identity = endpoint.Identity,
                    Address = "127.0.0.1", Port = endpoint.Port, PeerId = endpoint.PeerId,
                    Password = endpoint.Password, Rid = "890" }],
                Zones = [new ZoneConfiguration { Name = "Load", Channels = Enumerable.Range(1, count)
                    .Select(index => new ChannelConfiguration { Name = $"Listener {index}", System = "Performance",
                        Mode = "dmr", Slot = 1, Tgid = (100 + (index - 1) % routes).ToString(System.Globalization.CultureInfo.InvariantCulture), RxOnly = true, Algo = "none" }).ToList() }]
            };
            var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            FixtureRadioFactory? fixture = null;
            var preferenceStore = new ManagedReceivePreferences(Path.Combine(root, "UserSettings.json"));
            var configurationId = ConfigurationId.New();
            ConsoleReceiveSessionDependencies Prepare(ConsoleSessionState state, ConsoleSessionServices ownership)
            {
                var radios = FneConsoleRadioSessions.Prepare(state,
                    configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                    channel => new ChannelConfigurationAccess(channel.Runtime.Definition, keys));
                fixture = new FixtureRadioFactory(radios, state.Topology.Channels.Select(channel => channel.Id).ToArray(), true);
                var capture = ownership.Recording.OwnAsync("performance-capture", new CallRecordingManager(recordings));
                var host = new ConsoleHostServices(fixture, new IosAudioBackendFactory(audio),
                    new NativeVocoderFactory(NativeVocoderLinkage.StaticallyLinked),
                    new ManagedConfigurationLibrary(Path.Combine(root, "Configurations")),
                    new ManagedAssetStore(Path.Combine(root, "Assets")), recordings, new ForegroundLifecycle(),
                    SystemClock.Instance, new BackgroundApplicationScheduler(faults.Enqueue),
                    SystemApplicationDelay.Instance, new NoMicrophone(), []);
                var preferences = preferenceStore.ForConfiguration(configurationId, state.Channels.ToDictionary(
                    pair => pair.Key, pair => $"{pair.Value.Runtime.Definition.SystemName}\u001F{pair.Value.Runtime.Definition.Name}"));
                return new(host, radios.Systems, FneReceiveFrameNormalization.Instance, P25: keys, Recordings: capture, Preferences: preferences);
            }
            await using var runtime = await ConsoleReceiveSession.CreateAsync(configuration, Prepare);
            if (runtime.CaptureTopology().Channels.Count != count)
                throw new InvalidOperationException("Load topology lost configured listeners.");
            await using var session = new ConsoleApplicationSession(runtime);
            await runtime.SetConnectionChimesAsync(false);
            await runtime.ConnectAsync();
            await fixture!.Radio!.Connected.WaitAsync(TimeSpan.FromSeconds(20));
            foreach (var channel in runtime.CaptureTopology().Channels)
            {
                await runtime.SetChannelGainAsync(channel.Id, 0.5 / count);
                await runtime.SetRecordingEnabledAsync(channel.Id, true);
                await runtime.SetReceiveEnabledAsync(channel.Id, true);
            }
            MobileConsoleView? view = null;
            Control? previous = null;
            Border? shell = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                shell = (Border)((ISingleViewApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).MainView!;
                previous = shell.Child;
                view = new MobileConsoleView(UIKit.UIDevice.CurrentDevice.UserInterfaceIdiom == UIKit.UIUserInterfaceIdiom.Pad
                    ? ConsoleHostFormFactor.Tablet : ConsoleHostFormFactor.Phone,
                    execution: runtime.Execution, applicationSession: session, ownsSession: false);
                shell.Child = view;
            });
            try
            {
                long meters = 0;
                runtime.MeterSampled += (_, sample) => { if (sample.Peak > 0) Interlocked.Increment(ref meters); };
                await Task.Delay(2000); // Exclude startup/layout from steady-state samples.
                foreach (string phase in new[] { "idle", "receive-tar" })
                {
                    await File.WriteAllTextAsync(Path.Combine(documents, "performance-phase.txt"), $"{count},{routes},{phase}");
                    var audioStart = audio.CaptureDiagnostics();
                    long allocated = GC.GetTotalAllocatedBytes(precise: true);
                    int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                    long meterStart = Interlocked.Read(ref meters);
                    var clock = Stopwatch.StartNew();
                    if (phase == "idle") await Task.Delay(10000);
                    else
                    {
                        for (ushort packet = 0; packet < 250; packet++)
                        {
                            for (int route = 0; route < routes; route++)
                                await master.SendTrafficAsync(FneTrafficProtocol.Dmr, packets[route][packet % 6], packet, (uint)(99 + route));
                            TimeSpan remaining = TimeSpan.FromMilliseconds((packet + 1) * 60) - clock.Elapsed;
                            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                        }
                    }
                    double seconds = clock.Elapsed.TotalSeconds;
                    long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    long meterCount = Interlocked.Read(ref meters) - meterStart;
                    var health = await runtime.CaptureHealthAsync();
                    var audioEnd = audio.CaptureDiagnostics();
                    await File.AppendAllTextAsync(report, FormattableString.Invariant(
                        $"{count},{routes},{phase},{seconds:F3},{bytes},{GC.CollectionCount(0)-gc0},{GC.CollectionCount(1)-gc1},{GC.CollectionCount(2)-gc2},{meterCount},{health.ReceiveQueue.PeakDepth},{health.ReceiveLatency.P95.TotalMilliseconds:F3},{health.RouteRecoveryAttempts},{(audioEnd.StarvedDuration-audioStart.StarvedDuration).TotalMilliseconds:F3},{audioEnd.PendingStarvedDuration.TotalMilliseconds:F3},{audioEnd.DroppedInputSamples-audioStart.DroppedInputSamples},{audioEnd.OutputCallbacks-audioStart.OutputCallbacks}\n"));
                    if (phase != "idle" && meterCount == 0) throw new InvalidOperationException("Load traffic never produced receive audio.");
                    if (!faults.IsEmpty) throw new AggregateException(faults);
                }
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(documents, "performance-phase.txt"), $"{count},{routes},cleanup");
                await Dispatcher.UIThread.InvokeAsync(() => shell!.Child = previous);
                if (view is not null) await view.DisposeAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
