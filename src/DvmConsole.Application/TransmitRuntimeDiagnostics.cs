// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

/// <summary>Explicit host qualification with synthetic capture and an in-memory radio sink.</summary>
public static partial class TransmitRuntimeDiagnostics
{
    private static readonly string[] DigitalModes = ["p25", "dmr", "nxdn"];
    public static async Task<string> RunAsync(Func<IVocoderBackend> createVocoder)
    {
        ArgumentNullException.ThrowIfNull(createVocoder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SyntheticBackend? backend = null;
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => backend = new SyntheticBackend(),
            createVocoderBackend: createVocoder);
        TransmitChannelDescriptor[] channels = DigitalModes
            .Select((mode, index) =>
            {
                var configuration = new ChannelConfiguration
                {
                    Name = $"Qualification {mode}",
                    System = "Synthetic",
                    Tgid = (100 + index).ToString(),
                    Mode = mode,
                    Slot = 1
                };
                var state = new ConsoleChannelState(ChannelRuntimeDefinition.FromConfiguration(configuration));
                return state.CaptureTransmitDescriptor(new ChannelConfigurationAccess(state.Runtime.Definition));
            }).ToArray();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            var radio = new SyntheticRadio(channels);
            coordinator.SetMicrophoneAudioSuppressed(true);
            await coordinator.StartAsync(channels.Select(channel => new TransmitTarget(channel, radio)))
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            SyntheticBackend current = backend ?? throw new InvalidOperationException("Capture backend was not constructed.");
            Require(current.OpenCount == 1 && coordinator.ActiveChannels.Count == 3,
                "Multi-target startup did not share one capture.");
            Require(radio.PacketCounts.IsEmpty, "Prepared capture emitted a call before activation.");
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            Task pump = current.Capture.PumpAsync(pumping.Token);
            try
            {
                await coordinator.WaitForMicrophoneReadyAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                await coordinator.ActivateAsync(deadline.Token).ConfigureAwait(false);
                await coordinator.ReleaseMicrophoneAudioAsync(false, cancellationToken: deadline.Token).ConfigureAwait(false);
                await radio.VoiceObserved.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                await coordinator.StopAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
                Require(coordinator.ActiveChannels.Count == 0 && current.IsDisposed &&
                    current.Capture.IsDisposed && !current.Capture.HasObservers,
                    "Stopping left a transmitter, capture subscription or backend owned.");
                Require(radio.PacketCounts.Count == 3, "A digital protocol did not emit traffic.");
            }
            finally
            {
                pumping.Cancel();
                try { await pump.ConfigureAwait(false); }
                catch (OperationCanceledException) when (pumping.IsCancellationRequested) { }
            }
        }
        await RunSessionAsync(createVocoder, deadline.Token).ConfigureAwait(false);
        return "PASS\nComposed session: P25/DMR/NXDN channel and multi-select manual calls, TX recording sink, completed history, capture retirement, generated digital tones, resampled custom WAV audio and interrupted active/queued tone cancellation. Shared transmit coordinator: three P25/DMR/NXDN start-stop cycles, one synthetic capture per cycle, readiness/activation gating, encoded traffic and complete capture/backend retirement. In-memory radio sink only; no microphone, external network or RF interoperability qualification.";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SyntheticRadio(TransmitChannelDescriptor[] channels) : IRadioSession
    {
        private int nextStream;
        public string Name => "Synthetic";
        public SystemId SystemId => SystemId.FromName(Name);
        public bool IsConnectionActive => true;
        public event EventHandler<RadioTrafficRecord>? TrafficReceived { add { } remove { } }
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged { add { } remove { } }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ConcurrentDictionary<RadioMediaProtocol, TaskCompletionSource> ProtocolObserved { get; } = new();
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => channels;
        public IReadOnlyCollection<ChannelId> ChannelIds => channels.Select(channel => channel.Id).ToArray();
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public ConcurrentDictionary<RadioMediaProtocol, int> PacketCounts { get; } = new();
        public TaskCompletionSource VoiceObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => TargetAuthorityState.Available;
        public uint CreateStreamId() => (uint)Interlocked.Increment(ref nextStream);
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
        {
            Require(!payload.IsEmpty && streamId != 0, "Transmit emitted empty or unidentified traffic.");
            int count = PacketCounts.AddOrUpdate(protocol, 1, (_, count) => count + 1);
            if (count >= 3) ProtocolObserved.GetOrAdd(protocol, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (PacketCounts.Count == 3 && PacketCounts.Values.All(count => count >= 3))
                VoiceObserved.TrySetResult();
        }
    }

    private sealed class SyntheticBackend : IAudioBackend
    {
        public SyntheticCapture Capture { get; } = new();
        public Action<SyntheticBackend>? CaptureOpened { get; init; }
        public int OpenCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "Synthetic qualification capture";
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => direction == AudioDirection.Input ? [new("synthetic", Name, direction, true, false)] : [];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
        {
            Require(format == PcmAudioFormat.Voice8KhzMono16Bit, "Unexpected capture format.");
            OpenCount++;
            CaptureOpened?.Invoke(this);
            return Capture;
        }
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException("Transmit qualification has no physical output.");
        public void Dispose() => IsDisposed = true;
    }

    private sealed class SyntheticCapture : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public bool HasObservers => SamplesAvailable is not null;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
        public async Task PumpAsync(CancellationToken token)
        {
            var samples = new short[160];
            for (int index = 0; index < samples.Length; index++)
                samples[index] = (short)(6000 * Math.Sin(2 * Math.PI * 400 * index / 8000));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                if (IsRunning) SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
        }
    }
}
