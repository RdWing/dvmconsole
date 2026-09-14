// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Immutable;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

public static partial class TransmitRuntimeDiagnostics
{
    private sealed class SessionManualPreferences : IConsoleReceivePreferences, IConsoleManualTransmitOptionsStore
    {
        public ValueTask<ImmutableDictionary<ChannelId, ChannelReceivePreferences>> LoadAsync(CancellationToken token)
            => ValueTask.FromResult(ImmutableDictionary<ChannelId, ChannelReceivePreferences>.Empty);
        public ValueTask SaveAsync(ChannelId id, ChannelReceivePreferenceChange change, CancellationToken token)
            => ValueTask.CompletedTask;
        public ValueTask<ConsoleManualTransmitOptions> LoadManualTransmitOptionsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ConsoleManualTransmitOptions());
        public ValueTask SaveManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private static async Task RunSessionAsync(Func<IVocoderBackend> createVocoder, CancellationToken token)
    {
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "Synthetic", Identity = "Qualification", Address = "127.0.0.1",
                Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new ZoneConfiguration { Name = "Qualification", Channels = DigitalModes.Select((mode, index) =>
                new ChannelConfiguration { Name = mode, System = "Synthetic", Mode = mode, Tgid = (100 + index).ToString(), Slot = 1 }).ToList() }]
        };
        var audio = new SessionAudioFactory();
        var recordings = new SessionRecordingSink();
        var assets = new SessionAlertAssets();
        SyntheticRadio? radio = null;
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (state, services) =>
        {
            foreach (var channel in state.Channels.Values) channel.Operator.SetRecordingEnabled(true);
            radio = new SyntheticRadio(state.Channels.Values.Select(channel => channel.CaptureTransmitDescriptor(
                new ChannelConfigurationAccess(channel.Runtime.Definition))).ToArray());
            var descriptor = new RadioSystemDescriptor(radio.SystemId, radio.Name, "Synthetic", new Dictionary<string, string>());
            var plan = new ConsoleRadioSessionPlan([new ConsoleRadioSessionBinding(descriptor, new SessionRadioFactory(radio))]);
            var host = new ConsoleHostServices(plan, audio, new SessionVocoderFactory(createVocoder), null!, assets, null!,
                new SessionLifecycle(), SystemClock.Instance, new BackgroundApplicationScheduler(_ => { }),
                SystemApplicationDelay.Instance, null!, []);
            return new(host, plan.Systems, new SessionNormalizer(), Recordings: recordings, Preferences: new SessionManualPreferences(), ManualInput: new AudioInputProcessingOptions());
        }, cancellationToken: token).ConfigureAwait(false);
        var levels = new ConcurrentDictionary<ChannelId, double>();
        session.MeterSampled += (_, sample) => levels[sample.ChannelId] = sample.Rms;
        var channels = session.CaptureTopology().Channels.ToArray();
        bool releaseSelectedThroughMember = false;
        foreach (var targets in channels.Select(channel => new[] { channel }).Append(channels).Append(channels))
        {
            bool selected = targets.Length > 1;
            var callOptions = new ConsoleManualTransmitOptions(false, !selected);
            await session.SetManualTransmitOptionsAsync(callOptions, token).ConfigureAwait(false);
            if (selected)
                foreach (var target in targets) await session.SetTransmitSelectedAsync(target.Id, true, token).ConfigureAwait(false);
            radio!.ProtocolObserved.Clear();
            recordings.Written.Clear();
            recordings.Stopped.Clear();
            audio.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> press = selected ? session.BeginSelectedPttAsync(token).AsTask()
                : session.BeginPttAsync(targets[0].Id, token).AsTask();
            Task first = await Task.WhenAny(press, audio.Next.Task).WaitAsync(token).ConfigureAwait(false);
            Require(first == audio.Next.Task, $"Session did not construct capture: {session.CaptureSnapshot().StatusText}");
            var backend = await audio.Next.Task.ConfigureAwait(false);
            await session.SetManualTransmitOptionsAsync(new(true, !callOptions.MuteReceiveWhileTransmitting), token).ConfigureAwait(false);
            Require(!session.ManualTransmitSession!.PlayPermitTone &&
                session.ManualTransmitSession.MuteReceiveWhileTransmitting == callOptions.MuteReceiveWhileTransmitting,
                "Settings edits changed an admitted manual call's audio policy.");
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task pump = backend.Capture.PumpAsync(pumping.Token);
            try
            {
                Require(await press.WaitAsync(token).ConfigureAwait(false), $"Manual session did not start: {session.CaptureSnapshot().StatusText}");
                foreach (var channel in targets)
                {
                    var protocol = ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Id.Value.Protocol);
                    await radio!.ProtocolObserved.GetOrAdd(protocol, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token).ConfigureAwait(false);
                    await recordings.Written.GetOrAdd(channel.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token).ConfigureAwait(false);
                }
                if (selected)
                {
                    await session.SetTransmitSelectedAsync(targets[0].Id, false, token).ConfigureAwait(false);
                    Require(session.CaptureSnapshot().Channels.Values.Count(channel => channel.Transmitting) == 3,
                        "Selection edits changed an admitted call.");
                    if (releaseSelectedThroughMember)
                        await session.EndPttAsync(targets[0].Id, CancellationToken.None).AsTask().WaitAsync(token).ConfigureAwait(false);
                    else await session.EndSelectedPttAsync(CancellationToken.None).AsTask().WaitAsync(token).ConfigureAwait(false);
                    releaseSelectedThroughMember = true;
                }
                else await session.EndPttAsync(targets[0].Id, CancellationToken.None).AsTask().WaitAsync(token).ConfigureAwait(false);
                Require(backend.IsDisposed && backend.Capture.IsDisposed && !backend.Capture.HasObservers,
                    "Composed session retained capture after release.");
                foreach (var channel in targets)
                {
                    Require(recordings.Stopped.ContainsKey(channel.Id), "TX recording did not receive its stop boundary.");
                    Require(levels.TryGetValue(channel.Id, out double level) && level == 0, "Released TX left a stale meter.");
                }
            }
            finally
            {
                pumping.Cancel();
                try { await pump.ConfigureAwait(false); }
                catch (OperationCanceledException) when (pumping.IsCancellationRequested) { }
            }
        }
        Require(session.History.Count == 9 && session.History.All(call => call.Direction == ConsoleCallDirection.Transmit && !call.IsActive),
            "Composed session did not complete all transmit history entries.");
        foreach (var channel in channels) await session.SetAlertSelectedAsync(channel.Id, true, token).ConfigureAwait(false);
        audio.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var tone = new GeneratedToneSequence([GeneratedToneStep.Dtmf('1', TimeSpan.FromSeconds(1))]);
        await session.SendToneAsync(tone, ConsoleToneTargets.Alert, token).ConfigureAwait(false);
        Require(session.CaptureSnapshot().StatusText.Contains("local monitor failed", StringComparison.Ordinal),
            "Tone completion hid the unavailable synthetic local monitor.");
        Require(!audio.Next.Task.IsCompleted, "Generated audio opened microphone capture.");
        Require(session.CaptureSnapshot().Channels.Values.All(channel => !channel.Transmitting), "Completed tones retained transmit state.");

        radio!.ProtocolObserved.Clear();
        await session.SendAlertAudioAsync(assets.Id, token).ConfigureAwait(false);
        foreach (var channel in channels)
            Require(radio.ProtocolObserved.TryGetValue(ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Id.Value.Protocol), out var observed)
                && observed.Task.IsCompletedSuccessfully, "Custom WAV audio did not reach every selected digital protocol.");
        Require(session.CaptureSnapshot().StatusText == "Alert audio transmission completed.",
            "Custom audio did not publish completion or changed its non-monitored playback policy.");
        Require(!audio.Next.Task.IsCompleted, "Custom audio opened microphone capture.");
        Require(session.CaptureSnapshot().Channels.Values.All(channel => !channel.Transmitting), "Custom audio retained transmit state.");

        radio!.ProtocolObserved.Clear();
        var heldTone = new GeneratedToneSequence([GeneratedToneStep.Tone(1000, TimeSpan.FromSeconds(5))]);
        Task activeTone = session.SendToneAsync(heldTone, ConsoleToneTargets.Alert, token);
        Task queuedTone = session.SendToneAsync(tone, ConsoleToneTargets.Alert, token);
        foreach (var channel in channels)
            await radio.ProtocolObserved.GetOrAdd(ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Id.Value.Protocol),
                _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token).ConfigureAwait(false);
        session.SetAudioAvailable(false, "Qualification interruption");
        foreach (Task operation in new[] { activeTone, queuedTone })
        {
            try
            {
                await operation.WaitAsync(token).ConfigureAwait(false);
                throw new InvalidOperationException("Interrupted tone intent completed instead of being cancelled.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        await session.CancelTonesAsync().WaitAsync(token).ConfigureAwait(false);
        Require(session.CaptureSnapshot().Channels.Values.All(channel => !channel.Transmitting), "Interrupted tones retained transmit state.");
    }

    private sealed class SessionRadioFactory(SyntheticRadio radio) : IRadioSessionFactory
    {
        public ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor system, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRadioSession>(radio);
    }

    // Exercise the production stream decoder under static AOT, including 16 kHz to 8 kHz conversion.
    private sealed class SessionAlertAssets : IAssetStore
    {
        public AssetId Id { get; } = new(Guid.NewGuid());
        public ValueTask<Stream> OpenReadAsync(AssetId id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(id == Id, "Unexpected alert asset.");
            const int sampleRate = 16_000;
            const int sampleCount = sampleRate / 2;
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("RIFF"u8); writer.Write(36 + sampleCount * 2); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
                writer.Write("data"u8); writer.Write(sampleCount * 2);
                for (int index = 0; index < sampleCount; index++)
                    writer.Write((short)(8_000 * Math.Sin(2 * Math.PI * 700 * index / sampleRate)));
            }
            stream.Position = 0;
            return ValueTask.FromResult<Stream>(stream);
        }
        public ValueTask<AssetDescriptor> ImportAsync(string displayName, string mediaType, Stream content,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<AssetDescriptor> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new(Id, "Qualification alert", "audio/wav", 16_044);
        }
    }
    private sealed class SessionVocoderFactory(Func<IVocoderBackend> create) : IVocoderFactory
    {
        public IVocoderBackend Create(IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null) => create();
    }
    private sealed class SessionAudioFactory : IAudioBackendFactory
    {
        public TaskCompletionSource<SyntheticBackend> Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAudioBackend Create(AudioBackendConfiguration configuration)
        {
            // Device inspection creates a short-lived backend before capture.
            // Pump the backend that actually opens input, never that probe.
            return new SyntheticBackend { CaptureOpened = backend => Next.TrySetResult(backend) };
        }
    }
    private sealed class SessionNormalizer : IRadioReceiveFrameNormalizer
    {
        public IRadioMediaFrame Normalize(IRadioMediaFrame frame) => frame;
    }
    private sealed class SessionLifecycle : IApplicationLifecycle
    {
        public bool IsActive => true;
        public event EventHandler? Activated { add { } remove { } }
        public event EventHandler? Deactivated { add { } remove { } }
        public event EventHandler? Suspending { add { } remove { } }
        public event EventHandler? Resumed { add { } remove { } }
        public event EventHandler? Stopping { add { } remove { } }
    }
    private sealed class SessionRecordingSink : IReceiveRecordingSession, ITransmitRecordingSink
    {
        public ConcurrentDictionary<ChannelId, TaskCompletionSource> Written { get; } = new();
        public ConcurrentDictionary<ChannelId, bool> Stopped { get; } = new();
        public bool CanWrite => true;
        public event Action<ChannelId>? StateChanged { add { } remove { } }
        public ReceiveRecordingState CaptureState(ChannelId channel) => default;
        public void WriteTransmitSamples(ChannelRecordingDescriptor channel, uint streamId, uint sourceId, ReadOnlySpan<short> samples)
        {
            Require(!samples.IsEmpty && streamId != 0 && sourceId != 0, "TX recording received invalid samples.");
            Written.GetOrAdd(channel.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
        public void StopTransmit(ChannelRecordingDescriptor channel) => Stopped[channel.Id] = true;
        public void WriteEpisodeSamples(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId, uint sourceId,
            ReadOnlyMemory<short> samples, long? receiveEpisodeId = null) => throw new InvalidOperationException("Unexpected RX capture.");
        public void ObserveEpisodeTraffic(ChannelRecordingDescriptor channel, uint episodeStreamId, uint physicalStreamId,
            IRadioMediaFrame traffic, long? receiveEpisodeId = null)
        { }
        public void StopEpisode(ChannelRecordingDescriptor channel, long receiveEpisodeId) { }
        public void StopChannel(ChannelRecordingDescriptor channel) { }
        public Task DrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
