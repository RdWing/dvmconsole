// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Media;
using DvmConsole.Storage;
using DvmConsole.Vocoder;
using UIKit;

namespace DvmConsole.iOS;

internal static partial class IosReceiveSessionDiagnostics
{
    public static async Task<string> RunBackgroundAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("DVM_SESSION_QUIET_SECONDS"), out int quietSeconds) ||
            quietSeconds is < 1 or > 300)
            throw new ArgumentException("Background qualification requires a quiet interval from 1 to 300 seconds.");
        string root = Path.Combine(Path.GetTempPath(), "neo-background-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(quietSeconds + 75));
            await using var master = new FneLoopbackMaster();
            await using var web = new IosLoopbackWebStream(quietSeconds + 60);
            using var audio = new IosAudioSessionOwner();
            await using var recordings = new OpusRecordingStore(Path.Combine(root, "Recordings"), null, 0);
            var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recordings.RecordingFinalized += (_, _) => recorded.TrySetResult();
            var lifecycle = new BackgroundFixtureLifecycle();
            using var background = UIApplication.Notifications.ObserveDidEnterBackground((_, _) => lifecycle.SetActive(false));
            using var foreground = UIApplication.Notifications.ObserveDidBecomeActive((_, _) => lifecycle.SetActive(true));
            var endpoint = master.CreateOptions("Background");
            var configuration = new ConsoleConfiguration
            {
                Systems = [new() { Name = "Background", Identity = endpoint.Identity, Address = "127.0.0.1",
                    Port = endpoint.Port, PeerId = endpoint.PeerId, Password = endpoint.Password, Rid = "890" }],
                Groups = [new() { Name = "Background patch" }],
                Zones = [new() { Name = "Qualification", Channels = [
                    new() { Name = "Source", System = "Background", Mode = "p25", Tgid = "100", RxOnly = true },
                    new() { Name = "Target", System = "Background", Mode = "dmr", Tgid = "200", Slot = 1 }],
                    WebStreams = [new() { Name = "Sustained web", Url = web.Url }] }]
            };
            int packets = 0;
            var outputStreams = new HashSet<uint>();
            var outputSync = new object();
            void ObserveTransmit(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort sequence, uint stream)
            {
                // Reject anything outside this fixture's one explicit destination.
                var bytes = payload.Span;
                if (protocol != RadioMediaProtocol.Dmr || bytes.Length < 11 ||
                    ((uint)bytes[8] << 16 | (uint)bytes[9] << 8 | bytes[10]) != 200)
                    throw new InvalidOperationException("Background patch transmitted outside its synthetic target.");
                lock (outputSync)
                {
                    if (outputStreams.Count >= 8 && !outputStreams.Contains(stream))
                        throw new InvalidOperationException("Background patch created unbounded transmit calls.");
                    outputStreams.Add(stream);
                }
                Interlocked.Increment(ref packets);
            }
            FixtureRadioFactory? radio = null;
            var preferences = new ManagedReceivePreferences(Path.Combine(root, "UserSettings.json"));
            await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (state, services) =>
            {
                var radios = FneConsoleRadioSessions.Prepare(state,
                    configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                    channel => new ChannelConfigurationAccess(channel.Runtime.Definition));
                radio = new FixtureRadioFactory(radios, state.Topology.Channels.Select(channel => channel.Id).ToArray(),
                    allowNetwork: true, observeTransmit: ObserveTransmit);
                var capture = services.Recording.OwnAsync("background-recording", new CallRecordingManager(recordings));
                var host = new ConsoleHostServices(radio, new IosAudioBackendFactory(audio),
                    new NativeVocoderFactory(NativeVocoderLinkage.StaticallyLinked),
                    new ManagedConfigurationLibrary(Path.Combine(root, "Configurations")),
                    new ManagedAssetStore(Path.Combine(root, "Assets")), recordings, lifecycle,
                    SystemClock.Instance, new BackgroundApplicationScheduler(_ => { }), SystemApplicationDelay.Instance,
                    new NoMicrophone(), []);
                return new(host, radios.Systems, FneReceiveFrameNormalization.Instance, Recordings: capture,
                    Preferences: preferences.ForConfiguration(ConfigurationId.New(), state.Channels.ToDictionary(pair => pair.Key,
                        pair => pair.Value.Runtime.Definition.SystemName + "\u001F" + pair.Value.Runtime.Definition.Name)),
                    ManualInput: new AudioInputProcessingOptions());
            });
            await session.ActivateListeningAsync(deadline.Token);
            await session.SetConnectionChimesAsync(false);
            await session.ConnectAsync(deadline.Token);
            var connectedRadio = radio!.Radio!;
            await connectedRadio.Connected.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            var channels = session.CaptureTopology().Channels;
            var source = channels.Single(channel => channel.Name == "Source").Id;
            var target = channels.Single(channel => channel.Name == "Target").Id;
            await session.SetOutputMutedAsync(true);
            await session.SetRecordingEnabledAsync(source, true);
            await session.SetReceiveEnabledAsync(source, true);
            await session.SaveGroupAsync("Background patch", [source, target], true, true, deadline.Token);
            var stream = session.WebStreams.Single().Id;
            await session.SetWebStreamVolumeAsync(stream, 0, deadline.Token);
            await session.SetWebStreamPlayingAsync(stream, true, deadline.Token);
            if (!session.WebStreams.Single().Playback.IsActive)
                throw new InvalidOperationException("Sustained web playback did not start: " + session.WebStreams.Single().Playback.Status);
            await File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "session-quiet-ready.txt"), $"Muted RX/TAR, sustained web playback and one-way patch ready; injecting after {quietSeconds} seconds.", deadline.Token);
            await Task.Delay(TimeSpan.FromSeconds(quietSeconds), deadline.Token);
            var applicationState = await Dispatcher.UIThread.InvokeAsync(() => UIApplication.SharedApplication.ApplicationState);
            if (applicationState != UIApplicationState.Background || lifecycle.IsActive || !session.Execution.Snapshot.CanForwardPatches)
                throw new InvalidOperationException("Background patch admission did not survive the quiet interval.");
            if (!session.WebStreams.Single().Playback.IsActive || Volatile.Read(ref packets) != 0)
                throw new InvalidOperationException("Quiet interval lost web playback or invented a patch transmission.");
            byte[] continuation = CreateBackgroundVoiceLdu();
            await master.SendTrafficAsync(FneTrafficProtocol.P25, continuation, 1, 99, deadline.Token);
            await WaitForBackgroundAsync(() => Volatile.Read(ref packets) >= 4, deadline.Token);
            // The source has sent voice only: no terminator/new-call boundary exists.
            // Resume joins patch teardown before its terminator-inclusive counter baseline.
            session.SetAudioAvailable(false, "Synthetic background interruption");
            await session.ResumeAudioAsync(audio.ResumeListeningAsync, deadline.Token);
            lock (outputSync) outputStreams.Clear();
            int afterInterruption = Volatile.Read(ref packets);
            var staleObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveStaleContinuation(object? sender, RadioTrafficRecord record)
            {
                if (record.Traffic is { StreamId: 99, PacketSequence: 2 }) staleObserved.TrySetResult();
            }
            connectedRadio.TrafficReceived += ObserveStaleContinuation;
            try
            {
                await master.SendTrafficAsync(FneTrafficProtocol.P25, continuation, 2, 99, deadline.Token);
                await staleObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                await Task.Delay(250, deadline.Token);
            }
            finally { connectedRadio.TrafficReceived -= ObserveStaleContinuation; }
            if (Volatile.Read(ref packets) != afterInterruption)
                throw new InvalidOperationException("Interrupted source continuation was forwarded after recovery.");
            await master.SendTrafficAsync(FneTrafficProtocol.P25, continuation, 1, 100, deadline.Token);
            await WaitForBackgroundAsync(() => Volatile.Read(ref packets) >= afterInterruption + 4, deadline.Token);
            if (!session.WebStreams.Single().Playback.IsActive)
                throw new InvalidOperationException("Web playback did not recover alongside the new patch call.");
            await session.QuiesceAsync(deadline.Token);
            await recorded.Task.WaitAsync(deadline.Token);
            lock (outputSync)
                if (outputStreams.Count != 1) throw new InvalidOperationException("Recovery created duplicate patch transmit calls.");
            return $"PASS\n{quietSeconds}s Background with muted RX/TAR and sustained zero-volume web playback; one-way P25-to-DMR patch forwarded fresh loopback traffic, rejected the interrupted source call and resumed one new call. TAR finalized. No microphone, external network, device suspension or hardware qualification.";
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] CreateBackgroundVoiceLdu()
    {
        using var backend = new SoftwareVocoderBackend(linkage: NativeVocoderLinkage.StaticallyLinked);
        using var vocoder = backend.CreateSession(VocoderMode.P25Imbe);
        byte[]? payload = null;
        using var encoder = new P25TxAudioSession(2, 100, 99, vocoder,
            (value, _, _) =>
            {
                if (payload is not null) throw new InvalidOperationException("Voice fixture exceeded one LDU.");
                payload = value.ToArray();
            });
        short[] samples = new short[160 * P25DfsiFrameCodec.CodewordsPerLdu];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * i / 8000));
        if (encoder.Process(samples) != 1 || payload is null)
            throw new InvalidOperationException("Voice fixture did not produce one P25 continuation LDU.");
        return payload;
    }

    private static async Task WaitForBackgroundAsync(Func<bool> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class BackgroundFixtureLifecycle : IApplicationLifecycle
    {
        public bool IsActive { get; private set; } = true;
        public event EventHandler? Activated;
        public event EventHandler? Deactivated;
        public event EventHandler? Suspending { add { } remove { } }
        public event EventHandler? Resumed { add { } remove { } }
        public event EventHandler? Stopping { add { } remove { } }
        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active) Activated?.Invoke(this, EventArgs.Empty);
            else Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }
}
