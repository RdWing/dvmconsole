// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Media;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Storage;
using DvmConsole.Vocoder;

namespace DvmConsole.iOS;

internal static partial class IosReceiveSessionDiagnostics
{
    public static async Task<string> RunAsync(bool useLoopback = false)
    {
        string requestedProtocol = Environment.GetEnvironmentVariable("DVM_NETWORK_PROTOCOL") ?? "dmr";
        FneTrafficProtocol protocol = useLoopback ? requestedProtocol switch
        {
            "dmr" => FneTrafficProtocol.Dmr,
            "p25" => FneTrafficProtocol.P25,
            "nxdn" => FneTrafficProtocol.Nxdn,
            _ => throw new ArgumentException("Unknown qualification protocol.")
        } : FneTrafficProtocol.Dmr;
        string protocolText = protocol.ToString().ToUpperInvariant();
        bool secure = Environment.GetEnvironmentVariable("DVM_NETWORK_SECURE") == "1";
        if (secure && (!useLoopback || protocol != FneTrafficProtocol.P25))
            throw new ArgumentException("Secure qualification currently requires loopback P25.");
        string root = Path.Combine(Path.GetTempPath(), "neo-session-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using FneLoopbackMaster? master = useLoopback ? new FneLoopbackMaster() : null;
            await using var webSource = new IosLoopbackWebStream();
            using var audio = new IosAudioSessionOwner();
            await using var listeningControls = new IosListeningControls(audio);
            await using var recordings = new OpusRecordingStore(Path.Combine(root, "Recordings"), null, 0);
            using var p25Keys = new P25KeyRing();
            var configuration = new ConsoleConfiguration
            {
                Systems = [new DvmConsole.Core.Configuration.SystemConfiguration { Name = "Smoke", Identity = "Console", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
                Zones = [new ZoneConfiguration { Name = "Test", Channels = [new ChannelConfiguration
                    { Name = "Receive", System = "Smoke", Mode = protocol.ToString().ToLowerInvariant(), Slot = 1, Tgid = "100", RxOnly = true,
                        Algo = secure ? "aes" : "none", KeyId = secure ? "1" : null }] }]
            };
            if (master is not null)
            {
                FneConnectionOptions endpoint = master.CreateOptions("Smoke");
                configuration.Systems[0].Identity = endpoint.Identity;
                configuration.Systems[0].Port = endpoint.Port;
                configuration.Systems[0].PeerId = endpoint.PeerId;
                configuration.Systems[0].Password = endpoint.Password;
                configuration.Systems[0].Rid = "890";
            }
            configuration.Zones[0].WebStreams.Add(new() { Name = "Qualification web stream", Url = webSource.Url });
            var preferenceStore = new ManagedReceivePreferences(Path.Combine(root, "UserSettings.json"));
            var configurationId = ConfigurationId.New();
            IConsoleReceivePreferences? preferences = null;
            var faults = new List<Exception>();
            FixtureRadioFactory? fixture = null;
            var meter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalized = new TaskCompletionSource<RecordingFinalizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            recordings.RecordingFinalized += (_, result) => finalized.TrySetResult(result);
            ConsoleReceiveSessionDependencies Prepare(ConsoleSessionState state, ConsoleSessionServices ownership)
            {
                ConsoleRadioSessionPlan radios = FneConsoleRadioSessions.Prepare(state,
                    configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                    channel => new ChannelConfigurationAccess(channel.Runtime.Definition, p25Keys));
                var capture = ownership.Recording.OwnAsync("smoke-capture", new CallRecordingManager(recordings));
                fixture = new FixtureRadioFactory(radios, state.Topology.Channels.Select(channel => channel.Id).ToArray(), useLoopback);
                var host = new ConsoleHostServices(fixture, new IosAudioBackendFactory(audio),
                    new NativeVocoderFactory(NativeVocoderLinkage.StaticallyLinked),
                    new ManagedConfigurationLibrary(Path.Combine(root, "Configurations")),
                    new ManagedAssetStore(Path.Combine(root, "Assets")), recordings, new ForegroundLifecycle(),
                    SystemClock.Instance, new BackgroundApplicationScheduler(exception => { lock (faults) faults.Add(exception); }),
                    SystemApplicationDelay.Instance, new NoMicrophone(), []);
                preferences = preferenceStore.ForConfiguration(configurationId,
                    state.Channels.ToDictionary(pair => pair.Key,
                        pair => $"{pair.Value.Runtime.Definition.SystemName}\u001F{pair.Value.Runtime.Definition.Name}"));
                return new(host, radios.Systems, FneReceiveFrameNormalization.Instance, P25: p25Keys, Recordings: capture, Preferences: preferences);
            }
            await ReportStageAsync("Constructing shared receive session");
            await using var session = await ConsoleReceiveSession.CreateAsync(configuration, Prepare);
            // Match the host's prepare/activate lifecycle before exercising
            // playback. Preparation alone deliberately leaves web audio closed.
            await session.ActivateListeningAsync();
            await session.SetConnectionChimesAsync(false);
            if (session.ConnectionChimes || await ((IConsoleConnectionCuePreferences)preferences!).LoadConnectionChimesAsync())
                throw new InvalidOperationException("Connection chime settings did not persist.");
            await session.SetConnectionChimesAsync(true);
            if (!session.ConnectionChimes || !await ((IConsoleConnectionCuePreferences)preferences!).LoadConnectionChimesAsync())
                throw new InvalidOperationException("Connection chime settings did not restore.");
            if (!session.CanSaveDiagnosticSettings) throw new InvalidOperationException("Radio diagnostic settings were not exposed.");
            await session.SetVerboseLoggingAsync(true);
            if (!fixture!.Radio!.VerboseLoggingEnabled ||
                !await ((IConsoleDiagnosticPreferences)preferences!).LoadVerboseLoggingAsync())
                throw new InvalidOperationException("Verbose diagnostics were not applied and saved.");
            await session.SetVerboseLoggingAsync(false);
            if (fixture.Radio.VerboseLoggingEnabled) throw new InvalidOperationException("Verbose diagnostics did not stop.");
            var transportLog = new TaskCompletionSource<ConsoleLogEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.LogPublished += (_, entry) => transportLog.TrySetResult(entry);
            fixture!.Radio!.EmitLog(new(DateTimeOffset.UtcNow, "Smoke", DebugLogSeverity.Debug, "Portable transport diagnostic"));
            if ((await transportLog.Task.WaitAsync(TimeSpan.FromSeconds(10))).Level != ConsoleLogLevel.Debug)
                throw new InvalidOperationException("Portable transport diagnostics did not preserve severity.");
            if (master is not null)
            {
                await ReportStageAsync("Connecting to the local FNE fixture");
                await session.ConnectAsync();
                Task connected = fixture.Radio.Connected;
                Task completed = await Task.WhenAny(connected, master.Completion).WaitAsync(TimeSpan.FromSeconds(20));
                await completed;
                if (completed != connected) throw new InvalidOperationException("Loopback FNE stopped before connection completed.");
                if (secure)
                {
                    await ReportStageAsync("Requesting P25 key from the local FNE fixture");
                    using var keyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var requestedKey = await master.ReadKeyRequestAsync(keyTimeout.Token);
                    if (requestedKey != ((byte)0x84, (ushort)1))
                        throw new InvalidOperationException("Configured P25 key request did not match the fixture.");
                    await master.SendKeyResponseAsync(0x84, 1, IosLoopbackTransmit.P25TestKey, keyTimeout.Token);
                    while (!p25Keys.TryResolve("Smoke", 0x84, 1, out _))
                        await Task.Delay(10, keyTimeout.Token);
                }
                await ReportStageAsync("Verifying subscriber commands against the local FNE fixture");
                await FneSubscriberCommandDiagnostics.RunAsync(session, master, fixture.Radio.SystemId);
            }
            ChannelId channel = session.CaptureTopology().Channels.Single().Id;
            session.MeterSampled += (_, sample) => { if (sample.Peak > 0) meter.TrySetResult(); };
            bool muted = Environment.GetEnvironmentVariable("DVM_SESSION_MUTED") == "1";
            double gain = muted ? 0 : 0.75;
            await session.SetChannelGainAsync(channel, gain);
            await session.SetChannelBalanceAsync(channel, -0.25);
            await session.SetRecordingEnabledAsync(channel, true);
            await ReportStageAsync("Starting native receive output");
            await session.SetReceiveEnabledAsync(channel, true);
            if (!session.CaptureSnapshot().Channels[channel].ReceiveEnabled) throw new InvalidOperationException("Listening did not become active.");
            var beforeMute = session.CaptureSnapshot();
            await session.SetOutputMutedAsync(true);
            if (session.CaptureSnapshot().Channels[channel].EffectiveMuteReason != "global output mute" ||
                beforeMute.Channels[channel].EffectiveMuteReason is not null)
                throw new InvalidOperationException("Shared mute snapshots were missing or mutated an earlier snapshot.");
            var bufferingSystem = session.CaptureTopology().Systems.Single();
            var beforeBuffering = session.ReceiveBuffering;
            var fixedBuffering = new ConsoleReceiveBufferingOptions(P25Adaptive: false, DmrAdaptive: false, NxdnAdaptive: false);
            await session.SetReceiveBufferingAsync(bufferingSystem.Id, fixedBuffering);
            var savedBuffering = await ((IConsoleReceiveBufferingStore)preferences!).LoadReceiveBufferingAsync([bufferingSystem.Name]);
            if (session.ReceiveBuffering[bufferingSystem.Id] != fixedBuffering ||
                savedBuffering[bufferingSystem.Name] != fixedBuffering || beforeBuffering[bufferingSystem.Id] == fixedBuffering)
                throw new InvalidOperationException("Receive buffering did not preserve immutable state and managed preferences.");
            var retentionPreview = await session.PreviewRecordingRetentionAsync(0);
            if (retentionPreview.Cutoff is not null || retentionPreview.CandidateCount != 0)
                throw new InvalidOperationException("Keep-forever retention selected deletion candidates.");
            await session.SetRecordingRetentionAsync(new(0, true));
            var savedRetention = await ((IConsoleRecordingSettingsStore)preferences!).LoadRecordingRetentionAsync();
            if (savedRetention != session.RecordingRetention || savedRetention != new RecordingRetentionPolicy(0, true) || recordings.RetentionDays != 0)
                throw new InvalidOperationException("Recording retention did not persist through the shared settings owner.");
            VocoderMode processingMode = channel.Value.Protocol switch
            {
                ChannelProtocol.P25 => VocoderMode.P25Imbe,
                ChannelProtocol.Nxdn => VocoderMode.NxdnAmbe,
                _ => VocoderMode.DmrAmbe
            };
            var originalProcessing = session.ReceiveProcessing;
            var changedProcessing = originalProcessing[processingMode] with { PeakingGainDb = 4 };
            await session.SetReceiveProcessingAsync(processingMode, changedProcessing);
            if (session.ReceiveProcessing[processingMode] != changedProcessing ||
                originalProcessing[processingMode] == changedProcessing ||
                !session.CaptureSnapshot().Channels[channel].ReceiveEnabled ||
                session.CaptureSnapshot().Channels[channel].EffectiveMuteReason != "global output mute")
                throw new InvalidOperationException("Receive processing restart lost saved options, listening intent or mute policy.");
            var savedProcessing = await ((IConsoleReceiveProcessingStore)preferences!).LoadReceiveProcessingAsync();
            if (savedProcessing[processingMode] != changedProcessing)
                throw new InvalidOperationException("Receive processing did not persist through the managed settings owner.");
            await session.SetRequireConfiguredDmrReceiveKeyAsync(true);
            if (!session.RequireConfiguredDmrReceiveKey ||
                !await ((IConsoleDmrReceiveKeyPreferences)preferences!).LoadRequireConfiguredDmrReceiveKeyAsync() ||
                !session.CaptureSnapshot().Channels[channel].ReceiveEnabled ||
                session.CaptureSnapshot().Channels[channel].EffectiveMuteReason != "global output mute")
                throw new InvalidOperationException("DMR key-policy replacement lost settings, listening intent or output mute.");
            await session.SetRequireConfiguredDmrReceiveKeyAsync(false);
            if (session.RequireConfiguredDmrReceiveKey ||
                await ((IConsoleDmrReceiveKeyPreferences)preferences!).LoadRequireConfiguredDmrReceiveKeyAsync())
                throw new InvalidOperationException("DMR key policy did not restore on-air key selection.");
            await session.SetOutputMutedAsync(false);
            if (session.CaptureSnapshot().Channels[channel].EffectiveMuteReason is not null)
                throw new InvalidOperationException("Shared mute snapshot did not clear on restore.");
            await listeningControls.BindAsync(session);
            if (useLoopback)
            {
                if (listeningControls.Pause() != MediaPlayer.MPRemoteCommandHandlerStatus.Success ||
                    session.Execution.Snapshot.State != ConsoleExecutionState.RequiresResume)
                    throw new InvalidOperationException("Remote pause did not close listening admission.");
                await session.SetRequireConfiguredDmrReceiveKeyAsync(true);
                if (session.Execution.Snapshot.CanReceive || !session.RequireConfiguredDmrReceiveKey)
                    throw new InvalidOperationException("Changing paused DMR policy reopened listening.");
                bool remoteResumed = await listeningControls.ResumeOnForegroundAsync();
                if (!remoteResumed || !session.Execution.Snapshot.CanReceive)
                    throw new InvalidOperationException($"Remote play did not recover listening. Accepted={remoteResumed}; execution={session.Execution.Snapshot.State}; audio={audio.State}; receiveSelected={session.CaptureSnapshot().Channels[channel].ReceiveEnabled}; accepting={session.IsAcceptingCommands}.");
                await session.SetRequireConfiguredDmrReceiveKeyAsync(false);
            }
            await ReportStageAsync("Verifying loopback HTTP playback and recovery");
            await webSource.VerifyAsync(session, listeningControls);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var center = MediaPlayer.MPRemoteCommandCenter.Shared;
                if (center.PlayCommand.Enabled != useLoopback || center.PauseCommand.Enabled != useLoopback || center.NextTrackCommand.Enabled ||
                    center.ChangePlaybackPositionCommand.Enabled || (MediaPlayer.MPNowPlayingInfoCenter.DefaultCenter.NowPlaying?.IsLiveStream == true) != useLoopback)
                    throw new InvalidOperationException("System listening controls were not registered as a live stream.");
            });
            await session.FlushSettingsAsync(default);
            var checkpoints = new IosBackgroundCheckpoint();
            await IosBackgroundCheckpointDiagnostics.RunAsync();
            var checkpointEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkpointRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int checkpointCompletions = 0;
            int duplicateCheckpoints = 0;
            Task pendingCheckpoint = checkpoints.RunAsync(async token =>
            {
                checkpointEntered.TrySetResult();
                await checkpointRelease.Task.WaitAsync(token);
                await session.CheckpointAsync(token);
                checkpointCompletions++;
            });
            try
            {
                await checkpointEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await checkpoints.RunAsync(_ => { duplicateCheckpoints++; return Task.CompletedTask; });
            }
            finally { checkpointRelease.TrySetResult(); }
            await pendingCheckpoint;
            if (checkpointCompletions != 1 || duplicateCheckpoints != 0)
                throw new InvalidOperationException("Background checkpoint ownership was not preserved.");
            var saved = (await preferences!.LoadAsync(default))[channel];
            if ((saved with { IgnoredSubscriberIds = default }) != new ChannelReceivePreferences(gain, -0.25, true, true))
                throw new InvalidOperationException("Channel settings did not survive an on-disk reload.");
            int quietSeconds = int.TryParse(Environment.GetEnvironmentVariable("DVM_SESSION_QUIET_SECONDS"), out int seconds)
                ? Math.Clamp(seconds, 0, 300) : 0;
            if (quietSeconds > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "session-quiet-ready.txt"), $"RX and TAR ready; injecting traffic after {quietSeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(quietSeconds));
            }
            string executionState = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => UIKit.UIApplication.SharedApplication.ApplicationState.ToString());
            if (quietSeconds > 0 && executionState != nameof(UIKit.UIApplicationState.Background))
                throw new InvalidOperationException($"Expected background execution after quiet interval; app is {executionState}.");
            if (master is not null && protocol != FneTrafficProtocol.Dmr)
                await IosLoopbackTransmit.SendAsync(master, protocol, secure);
            else
            using (var encoder = new SoftwareVocoderBackend(linkage: NativeVocoderLinkage.StaticallyLinked))
            using (var voice = encoder.CreateSession(VocoderMode.DmrAmbe))
            {
                short[] pcm = new short[160];
                byte[] ambe = new byte[27];
                for (ushort packet = 1; packet <= 12; packet++)
                {
                    for (int frame = 0; frame < 3; frame++)
                    {
                        for (int sample = 0; sample < pcm.Length; sample++)
                            pcm[sample] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * (((packet - 1) * 3 + frame) * 160 + sample) / 8000));
                        if (voice.Encode(pcm, ambe.AsSpan(frame * 9, 9)) != 9)
                            throw new InvalidOperationException("Synthetic DMR encoding failed.");
                    }
                    byte[] payload = DmrVoicePacketCodec.CreateVoicePacket(2, 100, 0, true, 0, (byte)packet, ambe);
                    if (master is not null)
                        await master.SendTrafficAsync(FneTrafficProtocol.Dmr, payload, packet, 99);
                    else fixture!.Radio!.Emit(new FneTrafficFrame(FneTrafficProtocol.Dmr, 1, 2, 100, 0,
                        "GROUP", "VOICE", "VOICE", packet, 99, payload));
                }
            }
            await meter.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (session.History.Count != 1) throw new InvalidOperationException($"Synthetic {protocolText} did not create one receive call.");
            if (secure && session.History.Single().Encryption != RecordingEncryptionDescriptor.Secure(0x84, 1))
                throw new InvalidOperationException("P25 AES history lost its encryption metadata.");
            int inspectSeconds = int.TryParse(Environment.GetEnvironmentVariable("DVM_SESSION_INSPECT_SECONDS"), out int inspect)
                ? Math.Clamp(inspect, 0, 300) : 0;
            if (inspectSeconds > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "session-inspect-ready.txt"), $"Real receive audio completed; system player retained for {inspectSeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(inspectSeconds));
            }
            // Preparing with saved RX/TAR intent must not acquire another
            // RemoteIO output while the first session still owns the mix.
            await using var replacement = await ConsoleReceiveSession.CreateAsync(configuration, Prepare);
            var restored = replacement.CaptureSnapshot().Channels[channel];
            if (!restored.ReceiveEnabled || !restored.TarArmed || restored.Gain != gain || restored.Balance != -0.25)
                throw new InvalidOperationException("Replacement did not restore persisted operator intent.");
            await ReportStageAsync("Quiescing receive and finalizing TAR");
            await session.DisconnectAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HasNowPlayingMetadata() ||
                    MediaPlayer.MPRemoteCommandCenter.Shared.PlayCommand.Enabled || audio.CaptureDiagnostics().OutputCallbacks != 0)
                    throw new InvalidOperationException($"Stop All retained audio: nowPlaying={HasNowPlayingMetadata()}, state={audio.State}, callbacks={audio.CaptureDiagnostics().OutputCallbacks}.");
            }, Avalonia.Threading.DispatcherPriority.Background);
            await session.QuiesceAsync(CancellationToken.None);
            RecordingFinalizationResult result = await finalized.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!result.IsPlayable || result.Error is not null)
                throw new InvalidOperationException($"Synthetic {protocolText} TAR failed: " + result.Diagnostic, result.Error);
            if (secure && (result.Metadata!.EffectiveEncryptionState != CallRecordingEncryptionState.Secure ||
                result.Metadata.EncryptionAlgorithmId != 0x84 || result.Metadata.EncryptionKeyIdValue != 1))
                throw new InvalidOperationException("P25 AES TAR lost its encryption metadata.");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HasNowPlayingMetadata() ||
                    MediaPlayer.MPRemoteCommandCenter.Shared.PlayCommand.Enabled || audio.State != IosAudioExecutionState.Idle)
                    throw new InvalidOperationException($"Stopped FNEs retained audio: nowPlaying={HasNowPlayingMetadata()}, state={audio.State}, callbacks={audio.CaptureDiagnostics().OutputCallbacks}.");
            }, Avalonia.Threading.DispatcherPriority.Background);
            session.ReactivateAfterFailedReplacement();
            await ReportStageAsync("Starting native receive output");
            await session.SetReceiveEnabledAsync(channel, true);
            var recordingId = new RecordingId(Guid.Parse(result.Metadata!.RecordingId));
            bool playbackHighlighted = false;
            session.ControlStateInvalidated += (_, _) =>
            {
                if (session.CaptureSnapshot().Channels[channel].RecordingPlayback) playbackHighlighted = true;
            };
            await ReportStageAsync("Starting shared-output recording playback");
            await session.PlayRecordingAsync(recordingId);
            using (var playbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (session.IsRecordingPlaying(recordingId) || session.CaptureSnapshot().Channels[channel].RecordingPlayback)
                    await Task.Delay(25, playbackDeadline.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (audio.State != IosAudioExecutionState.Idle || HasNowPlayingMetadata())
                    throw new InvalidOperationException("Offline recording EOF retained the audio session.");
            }, Avalonia.Threading.DispatcherPriority.Background);
            if (!playbackHighlighted || session.CaptureSnapshot().Channels[channel].RecordingPlayback)
                throw new InvalidOperationException("Recording channel highlight did not follow playback and EOF.");
            if (session.CaptureSnapshot().StatusText.Contains("Recording playback failed", StringComparison.Ordinal))
                throw new InvalidOperationException(session.CaptureSnapshot().StatusText);
            await ReportStageAsync("Starting shared-output recording playback");
            await session.PlayRecordingAsync(recordingId);
            await ReportStageAsync("Stopping recording playback");
            await session.StopRecordingPlaybackAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (audio.State != IosAudioExecutionState.Idle || HasNowPlayingMetadata())
                    throw new InvalidOperationException("Stopping an offline recording retained the audio session.");
            }, Avalonia.Threading.DispatcherPriority.Background);
            if (session.IsRecordingPlaying(recordingId)) throw new InvalidOperationException("Recording playback did not stop.");
            if (session.CaptureSnapshot().Channels[channel].RecordingPlayback)
                throw new InvalidOperationException("Recording channel highlight survived explicit stop.");
            using (var csv = new MemoryStream())
            {
                CallHistoryCsv.Write(csv, session.History.Select(CallHistoryExportRow.FromCall), leaveOpen: true);
                if (!System.Text.Encoding.UTF8.GetString(csv.ToArray()).Contains(protocolText, StringComparison.Ordinal))
                    throw new InvalidOperationException("Session history CSV omitted the synthetic call.");
            }
            var recordedCall = session.History.Single();
            if (!recordedCall.HasRecording)
                throw new InvalidOperationException("Finalized TAR did not publish shared History attachment.");
            await session.ClearSessionHistoryAsync();
            if (session.History.Count != 0 || !(await recordings.LoadRecordingsAsync()).Any(item => item.IsPlayable))
                throw new InvalidOperationException("Clearing session history did not retain playable TAR.");
            await ReportStageAsync("Capturing shared engineering health");
            var health = await session.CaptureHealthAsync();
            if (health.RecordingFinalization is null || health.RecordingCatalog is not { Loaded: > 0 } ||
                health.ReceiveQueue.PeakDepth == 0 || health.ReceiveLatency.P95 <= TimeSpan.Zero)
                throw new InvalidOperationException("Engineering health omitted receive or recording observations.");
            await ReportStageAsync("Disposing outgoing receive session");
            await session.DisposeAsync();
            await ReportStageAsync("Activating replacement output");
            await replacement.ActivateListeningAsync();
            await replacement.SetWebStreamPlayingAsync(replacement.WebStreams.Single().Id, true);
            await listeningControls.BindAsync(replacement);
            await listeningControls.UnbindAsync(session);
            if (await listeningControls.ResumeAsync(session))
                throw new InvalidOperationException("Retired remote controls resumed an outgoing session.");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!MediaPlayer.MPRemoteCommandCenter.Shared.PlayCommand.Enabled)
                    throw new InvalidOperationException("Retiring the outgoing player cleared replacement controls.");
            });
            await listeningControls.UnbindAsync(replacement);
            await ReportStageAsync("Disposing replacement session");
            await replacement.DisposeAsync();
            await ReportStageAsync("Exporting and deleting archive recording");
            var archive = session.CreateRecordingArchive(new OpusRecordingArchive(recordings));
            var savedRecording = (await archive.LoadAsync()).Single();
            if (savedRecording.Id != recordingId || !savedRecording.IsPlayable)
                throw new InvalidOperationException("Recording archive lost capture identity or playable state.");
            if (archive.Revision <= 0 || savedRecording.CallIdentity is not { } identity ||
                new RecordingCallIndex([recordedCall]).FindBest(identity) != recordedCall.Id)
                throw new InvalidOperationException("Finalized TAR did not attach to its operational call identity.");
            long finalizedRevision = archive.Revision;
            using (var exported = new MemoryStream())
            {
                await archive.ExportAsync(recordingId, exported);
                if (exported.Length == 0) throw new InvalidOperationException("Recording export was empty.");
            }
            if (!await archive.DeleteAsync(recordingId) || (await archive.LoadAsync()).Count != 0 || archive.Revision <= finalizedRevision)
                throw new InvalidOperationException("Recording archive deletion failed.");
            lock (faults) if (faults.Count != 0) throw new AggregateException(faults);
            return $"PASS\nQuiet interval {quietSeconds}s ({executionState}, muted={muted}); {(useLoopback ? "Real loopback FNE UDP reception and four subscriber command payloads and correlated acknowledgements" : "FNE construction and synthetic DMR")}, portable transport logs, {protocolText}{(secure ? " AES-256 with FNE/KMM key retrieval" : "")} through static vocoder, receive meter/history/CSV, system listening play/pause and replacement ownership, shared-output recording playback and stop, archive export/delete, TAR retained after history clear, persisted channel preferences, loopback HTTP/WAV playback and recovery, playable TAR, quiescence, deferred replacement playback, reactivation and disposal. {(useLoopback ? "Loopback network only; no external FNE or microphone." : "HTTP loopback only; no external network or microphone.")}";
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task ReportStageAsync(string stage)
        => File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "session-progress.txt"), stage);

    private static bool HasNowPlayingMetadata()
    {
        using var key = new Foundation.NSString("nowPlayingInfo");
        using var metadata = MediaPlayer.MPNowPlayingInfoCenter.DefaultCenter.ValueForKey(key);
        return metadata is Foundation.NSDictionary { Count: > 0 };
    }

    private sealed class FixtureRadioFactory(IRadioSessionFactory inner, ChannelId[] channels, bool allowNetwork, Action<RadioMediaProtocol, ReadOnlyMemory<byte>, ushort, uint>? observeTransmit = null) : IRadioSessionFactory
    {
        public FixtureRadio? Radio { get; private set; }
        public async ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor system, CancellationToken cancellationToken = default)
            => Radio = new FixtureRadio(await inner.CreateAsync(system, cancellationToken), channels, allowNetwork, observeTransmit);
    }

    private sealed class FixtureRadio : IRadioSession, IRadioLogSource, IRadioSubscriberCommandEndpoint, IRadioP25KeyEndpoint, IRadioConnectionStateNotifications, IRadioSubscriberAcknowledgementSource, IRadioDiagnosticSettings
    {
        public bool VerboseLoggingEnabled { get; private set; }
        public void SetVerboseLogging(bool enabled)
        {
            adapter.SetVerboseLogging(enabled);
            VerboseLoggingEnabled = enabled;
        }
        private readonly IRadioSession inner;
        private readonly Action<RadioMediaProtocol, ReadOnlyMemory<byte>, ushort, uint>? observeTransmit;
        private readonly ChannelId[] channels;
        private readonly bool allowNetwork;
        private readonly FneRadioSessionAdapter adapter;
        private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FixtureRadio(IRadioSession inner, ChannelId[] channels, bool allowNetwork, Action<RadioMediaProtocol, ReadOnlyMemory<byte>, ushort, uint>? observeTransmit = null)
        {
            this.inner = inner;
            this.observeTransmit = observeTransmit;
            this.channels = channels;
            this.allowNetwork = allowNetwork;
            adapter = (FneRadioSessionAdapter)inner;
            adapter.StatusChanged += OnStatus;
            adapter.P25KeyReceived += OnKey;
            adapter.SubscriberAcknowledged += OnSubscriberAcknowledged;
            inner.TrafficReceived += OnTraffic;
            inner.AuthorityChanged += OnAuthority;
        }
        public Task Connected => connected.Task;
        private void OnStatus(object? sender, FneConnectionStatus status)
        {
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
            if (status.State == FneConnectionState.Connected) connected.TrySetResult();
        }
        public RadioConnectionSnapshot ConnectionState => adapter.ConnectionState;
        public event EventHandler? ConnectionStateChanged;
        public event EventHandler<RadioP25KeyResponse>? P25KeyReceived;
        public event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;
        private void OnSubscriberAcknowledged(object? sender, ConsoleSubscriberAcknowledgement response)
            => SubscriberAcknowledged?.Invoke(this, response);
        private void OnKey(object? sender, RadioP25KeyResponse response) => P25KeyReceived?.Invoke(this, response);
        public void RequestP25Key(byte algorithm, ushort key)
        {
            if (!allowNetwork) throw new InvalidOperationException("Key qualification requires the loopback FNE.");
            adapter.RequestP25Key(algorithm, key);
        }
        public SystemId SystemId => inner.SystemId;
        public string Name => inner.Name;
        public bool IsConnected => inner.IsConnected;
        public bool IsConnectionActive => inner.IsConnectionActive;
        public uint? SourceId => inner.SourceId;
        public void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId)
        {
            if (!allowNetwork) throw new InvalidOperationException("Subscriber qualification requires the loopback FNE.");
            ((IRadioSubscriberCommandEndpoint)inner).SendSubscriberCommand(command, destinationId);
        }
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => inner.ChannelDescriptors;
        public IReadOnlyCollection<ChannelId> ChannelIds => channels;
        public event EventHandler<DebugLogEntry>? LogPublished;
        public void EmitLog(DebugLogEntry entry) => LogPublished?.Invoke(this, entry);
        public event EventHandler<RadioTrafficRecord>? TrafficReceived;
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
        private void OnTraffic(object? sender, RadioTrafficRecord record) => TrafficReceived?.Invoke(this, record);
        private void OnAuthority(object? sender, TalkgroupAuthorityRecord record) => AuthorityChanged?.Invoke(this, record);
        public void Emit(IRadioMediaFrame frame) => TrafficReceived?.Invoke(this, new(SystemId, channels, frame, DateTimeOffset.UtcNow));
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => allowNetwork
            ? inner.StartAsync(cancellationToken) : throw new InvalidOperationException("Offline diagnostics must not connect.");
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => inner.QuiesceAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            inner.TrafficReceived -= OnTraffic;
            inner.AuthorityChanged -= OnAuthority;
            adapter.StatusChanged -= OnStatus;
            adapter.P25KeyReceived -= OnKey;
            adapter.SubscriberAcknowledged -= OnSubscriberAcknowledged;
            return inner.DisposeAsync();
        }
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => inner.GetTargetAuthority(protocol, destinationId, runtimeSlot);
        public uint CreateStreamId() => inner.CreateStreamId();
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
        {
            if (!allowNetwork || observeTransmit is null)
                throw new InvalidOperationException("Diagnostics must not transmit.");
            observeTransmit(protocol, payload, packetSequence, streamId);
            inner.SendTraffic(protocol, payload, packetSequence, streamId);
        }
    }

    private sealed class NoMicrophone : IMicrophonePermissionService
    {
        public ValueTask<MicrophonePermissionState> GetStateAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MicrophonePermissionState.Unavailable);
        public ValueTask<MicrophonePermissionState> RequestAsync(CancellationToken cancellationToken = default)
            => GetStateAsync(cancellationToken);
    }
    private sealed class ForegroundLifecycle : IApplicationLifecycle
    {
        public bool IsActive => true;
        public event EventHandler? Activated { add { } remove { } }
        public event EventHandler? Deactivated { add { } remove { } }
        public event EventHandler? Suspending { add { } remove { } }
        public event EventHandler? Resumed { add { } remove { } }
        public event EventHandler? Stopping { add { } remove { } }
    }
}
