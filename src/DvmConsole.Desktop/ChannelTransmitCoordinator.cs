using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

public sealed record TransmitTarget(ChannelViewModel Channel, SystemViewModel System);

// Lazily owns explicit transmit calls. Direct PTT starts one target; global
// PTT may start several targets, all fed by one microphone capture stream.
public sealed class ChannelTransmitCoordinator : IAsyncDisposable
{
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver;
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAudioBackend? audioBackend;
    private IVocoderBackend? vocoderBackend;
    private SharedAudioCapture? sharedCapture;
    private readonly List<ActiveTransmit> active = [];
    private bool disposed;
    private AudioInputProcessingOptions audioInputOptions;

    public event EventHandler<Exception>? Faulted;
    public ChannelViewModel? ActiveChannel => active.FirstOrDefault()?.Channel;
    public IReadOnlyList<ChannelViewModel> ActiveChannels => active.Select(entry => entry.Channel).ToArray();
    public uint ActiveStreamId => active.FirstOrDefault()?.StreamId ?? 0;

    public ChannelTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        AudioInputProcessingOptions? audioInputOptions = null,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<IAudioBackend>? createAudioBackend = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.audioInputOptions = (audioInputOptions ?? new AudioInputProcessingOptions()).Normalize();
        this.samplesObserver = samplesObserver;
        this.createAudioBackend = createAudioBackend ??
            (() => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")));
    }

    public void UpdateAudioInputOptions(AudioInputProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        audioInputOptions = options.Normalize();
    }

    public uint GetActiveStreamId(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return active.FirstOrDefault(entry => ReferenceEquals(entry.Channel, channel))?.StreamId ?? 0;
    }

    public Task StartAsync(ChannelViewModel channel, SystemViewModel system)
        => StartAsync([new TransmitTarget(channel, system)]);

    public async Task StartAsync(IEnumerable<TransmitTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ObjectDisposedException.ThrowIf(disposed, this);
        TransmitTarget[] requested = targets
            .Where(target => target.Channel is not null && target.System is not null)
            .GroupBy(target => target.Channel)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ValidateTargets(requested);
            await StopCoreAsync().ConfigureAwait(false);

            IAudioBackend? createdAudioBackend = null;
            IVocoderBackend? createdVocoderBackend = null;
            SharedAudioCapture? createdSharedCapture = null;
            var created = new List<ActiveTransmit>();
            try
            {
                createdAudioBackend = createAudioBackend();
                AudioDeviceInfo input = SelectInput(
                    createdAudioBackend.EnumerateDevices(AudioDirection.Input),
                    audioInputOptions.DeviceId);
                var capture = new ProcessedAudioCapture(
                    createdAudioBackend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit),
                    audioInputOptions);
                if (samplesObserver is not null)
                {
                    capture.SamplesAvailable += (_, args) =>
                    {
                        foreach (TransmitTarget target in requested)
                        {
                            ActiveTransmit? entry = created.FirstOrDefault(
                                candidate => ReferenceEquals(candidate.Channel, target.Channel));
                            if (entry is not null)
                                samplesObserver(target.Channel, entry.StreamId, entry.SourceId, args.Samples);
                        }
                    };
                }
                createdSharedCapture = new SharedAudioCapture(capture);

                if (requested.Any(target => target.Channel.Definition.Mode != "analog"))
                    createdVocoderBackend = new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY"));

                foreach (TransmitTarget target in requested)
                {
                    bool isDmr = target.Channel.Definition.Mode == "dmr";
                    bool isAnalog = target.Channel.Definition.Mode == "analog";
                    uint sourceId = target.System.SourceId!.Value;
                    uint streamId = target.System.CreateStreamId();
                    SharedAudioCapture.Lease lease = createdSharedCapture.CreateLease();
                    Action<ReadOnlyMemory<byte>, ushort, uint> send = (payload, sequence, stream) => target.System.SendTraffic(
                        isDmr
                            ? FneTrafficProtocol.Dmr
                            : isAnalog
                                ? FneTrafficProtocol.Analog
                                : FneTrafficProtocol.P25,
                        payload.Span,
                        sequence,
                        stream);

                    ITransmitCaptureSession session;
                    if (isAnalog)
                    {
                        session = new AnalogTransmitCaptureSession(
                            lease,
                            sourceId,
                            target.Channel.Definition.DestinationId,
                            streamId,
                            send);
                    }
                    else
                    {
                        IVocoderSession vocoder = createdVocoderBackend!.CreateSession(
                            isDmr ? VocoderMode.DmrAmbe : VocoderMode.P25Imbe);
                        session = isDmr
                            ? new DmrTransmitCaptureSession(
                                lease,
                                vocoder,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                target.Channel.Definition.Slot,
                                streamId,
                                send)
                            : new P25TransmitCaptureSession(
                                lease,
                                vocoder,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                streamId,
                                send,
                                CreateP25EncryptionOptions(target.Channel));
                    }

                    session.Faulted += HandleSessionFaulted;
                    created.Add(new ActiveTransmit(target.Channel, streamId, sourceId, session));
                }

                foreach (ActiveTransmit entry in created)
                    await entry.Session.StartAsync().ConfigureAwait(false);

                audioBackend = createdAudioBackend;
                vocoderBackend = createdVocoderBackend;
                sharedCapture = createdSharedCapture;
                active.AddRange(created);
            }
            catch
            {
                await DisposeEntriesAsync(created).ConfigureAwait(false);
                if (createdSharedCapture is not null)
                    await createdSharedCapture.DisposeAsync().ConfigureAwait(false);
                createdVocoderBackend?.Dispose();
                createdAudioBackend?.Dispose();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private static void ValidateTargets(IEnumerable<TransmitTarget> targets)
    {
        foreach (TransmitTarget target in targets)
        {
            if (!target.Channel.CanTransmit)
                throw new InvalidOperationException($"{target.Channel.Name} is RX-only or cannot transmit with its configured encryption.");
            if (!target.System.Channels.Contains(target.Channel))
                throw new InvalidOperationException($"{target.Channel.Name} does not belong to FNE system '{target.System.Name}'.");
            // DMR transmission is intentionally fail-open with respect to the
            // master's announced talkgroup table. AnnouncedTGs is advisory for
            // this console; a missing/deactivated entry must not disable PTT.
            if (!target.System.IsConnected)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' is not connected.");
            if (target.System.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' has no valid transmit RID.");
        }
    }

    private async Task StopCoreAsync()
    {
        ActiveTransmit[] current = active.ToArray();
        active.Clear();
        Exception? failure = null;
        try
        {
            await DisposeEntriesAsync(current).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (sharedCapture is not null)
        {
            try
            {
                await sharedCapture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            sharedCapture = null;
        }
        vocoderBackend?.Dispose();
        vocoderBackend = null;
        audioBackend?.Dispose();
        audioBackend = null;
        if (failure is not null)
            throw failure;
    }

    private async Task DisposeEntriesAsync(IEnumerable<ActiveTransmit> entries)
    {
        Exception? failure = null;
        foreach (ActiveTransmit entry in entries.Reverse())
        {
            entry.Session.Faulted -= HandleSessionFaulted;
            try
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        if (failure is not null)
            throw failure;
    }

    private void HandleSessionFaulted(object? sender, Exception exception) => Faulted?.Invoke(this, exception);

    private static AudioDeviceInfo SelectInput(IReadOnlyList<AudioDeviceInfo> devices, string deviceId)
        => devices.FirstOrDefault(device => device.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException("No audio input device is available.");

    private P25TxEncryptionOptions? CreateP25EncryptionOptions(ChannelViewModel channel)
    {
        if (!channel.Definition.IsEncrypted || !channel.IsTransmitEncrypted)
            return null;
        if (p25KeyResolver is null ||
            !P25KeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out byte algorithmId) ||
            !P25KeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out ushort keyId) ||
            !p25KeyResolver.TryResolve(
                channel.Definition.SystemName,
                algorithmId,
                keyId,
                out ReadOnlyMemory<byte> key))
        {
            throw new NotSupportedException(
                $"P25 encrypted transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }
        return P25TxEncryptionOptions.CreateRandom(algorithmId, keyId, key);
    }

    private sealed record ActiveTransmit(
        ChannelViewModel Channel,
        uint StreamId,
        uint SourceId,
        ITransmitCaptureSession Session);
}
