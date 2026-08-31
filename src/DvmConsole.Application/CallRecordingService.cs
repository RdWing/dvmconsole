using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

public sealed record RecordingOperationState(
    ChannelId ChannelId,
    CallId CallId,
    RecordingId RecordingId,
    bool IsTransmit,
    bool IsFinalizing,
    string? Fault);

/// <summary>
/// Owns portable RX and TX recording capture lifecycles. Storage owns the
/// durable location and exposes only streams, so this service has no path or
/// platform dependency.
/// </summary>
public sealed class CallRecordingService : IAsyncDisposable
{
    private const string WaveMediaType = "audio/wav; codecs=pcm_s16le";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IRecordingStore store;
    private readonly IClock clock;
    private readonly Func<ChannelId, uint, bool> shouldRecordSource;
    private readonly Func<ChannelId, uint, string> resolveSubscriberAlias;
    private readonly Dictionary<(ChannelId ChannelId, long EpisodeId), ActiveRecording> receive = [];
    private readonly Dictionary<ChannelId, ActiveRecording> transmit = [];
    private readonly Dictionary<(ChannelId ChannelId, long EpisodeId), ReceiveEncryptionState> encryption = [];
    private int retentionDays;
    private int disposed;

    public CallRecordingService(
        IRecordingStore store,
        IClock? clock = null,
        int retentionDays = 7,
        Func<ChannelId, uint, bool>? shouldRecordSource = null,
        Func<ChannelId, uint, string>? resolveSubscriberAlias = null)
    {
        if (retentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? SystemClock.Instance;
        this.retentionDays = retentionDays;
        this.shouldRecordSource = shouldRecordSource ?? ((_, _) => true);
        this.resolveSubscriberAlias = resolveSubscriberAlias ?? ((_, _) => string.Empty);
    }

    public event EventHandler<RecordingOperationState>? StateChanged;

    public int RetentionDays
    {
        get => Volatile.Read(ref retentionDays);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref retentionDays, value);
        }
    }

    public async ValueTask WriteReceiveSamplesAsync(
        ChannelRecordingDescriptor channel,
        long episodeId,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        CallId? callId = null,
        CancellationToken cancellationToken = default)
        => await WriteReceiveSamplesAsync(
            channel,
            episodeStreamId: episodeId <= uint.MaxValue ? (uint)episodeId : 0,
            physicalStreamId: episodeId <= uint.MaxValue ? (uint)episodeId : 0,
            sourceId,
            samples,
            receiveEpisodeId: episodeId,
            callId,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteReceiveSamplesAsync(
        ChannelRecordingDescriptor channel,
        uint episodeStreamId,
        uint physicalStreamId,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        long? receiveEpisodeId = null,
        CallId? callId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.RecordingEnabled || episodeStreamId == 0 || samples.IsEmpty)
            return;

        long episodeId = receiveEpisodeId ?? episodeStreamId;
        if (sourceId != 0 && !shouldRecordSource(channel.Id, sourceId))
        {
            await StopReceiveEpisodeAsync(channel.Id, episodeId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var key = (channel.Id, episodeId);
            if (!receive.TryGetValue(key, out ActiveRecording? recording))
            {
                recording = await CreateAsync(
                    channel,
                    callId ?? CallId.New(),
                    isTransmit: false,
                    episodeStreamId,
                    sourceId == 0 ? null : sourceId,
                    receiveEpisodeId,
                    cancellationToken).ConfigureAwait(false);
                receive.Add(key, recording);
            }

            if (recording.ObservePhysicalStream(physicalStreamId))
                TryUpdateContext(recording);

            Write(recording, samples.Span);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask WriteTransmitSamplesAsync(
        ChannelRecordingDescriptor channel,
        uint streamId,
        ReadOnlyMemory<short> samples,
        CallId? callId = null,
        CancellationToken cancellationToken = default)
        => await WriteTransmitSamplesAsync(
            channel,
            streamId,
            channel.ActiveSourceId ?? 0,
            samples,
            callId,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteTransmitSamplesAsync(
        ChannelRecordingDescriptor channel,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        CallId? callId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.RecordingEnabled || streamId == 0 || samples.IsEmpty)
            return;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (transmit.TryGetValue(channel.Id, out ActiveRecording? previous) &&
                previous.StreamId != streamId)
            {
                transmit.Remove(channel.Id);
                await FinalizeAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            if (!transmit.TryGetValue(channel.Id, out ActiveRecording? recording))
            {
                recording = await CreateAsync(
                    channel,
                    callId ?? CallId.New(),
                    isTransmit: true,
                    streamId,
                    sourceId: sourceId == 0 ? null : sourceId,
                    receiveEpisodeId: null,
                    cancellationToken).ConfigureAwait(false);
                transmit.Add(channel.Id, recording);
            }

            Write(recording, samples.Span);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask StopReceiveEpisodeAsync(
        ChannelId channelId,
        long episodeId,
        CancellationToken cancellationToken = default)
        => StopAsync((channelId, episodeId), cancellationToken);

    public async ValueTask ObserveReceiveTrafficAsync(
        ChannelRecordingDescriptor channel,
        long episodeId,
        uint physicalStreamId,
        IRadioMediaFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        if (episodeId <= 0)
            return;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = (channel.Id, episodeId);
            if (!encryption.TryGetValue(key, out ReceiveEncryptionState? state))
            {
                state = new ReceiveEncryptionState();
                encryption.Add(key, state);
            }

            bool changed = state.Observe(traffic);
            if (receive.TryGetValue(key, out ActiveRecording? recording))
            {
                changed |= recording.ObservePhysicalStream(physicalStreamId);
                if (state.Encryption.IsKnown)
                    changed |= recording.SetEncryption(state.Encryption);
                if (changed)
                    TryUpdateContext(recording);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StopTransmitAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (transmit.Remove(channelId, out ActiveRecording? recording))
                await FinalizeAsync(recording, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StopChannelAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveRecording[] recordings = receive
                .Where(entry => entry.Key.ChannelId == channelId)
                .Select(entry => entry.Value)
                .Concat(transmit.TryGetValue(channelId, out ActiveRecording? tx) ? [tx] : [])
                .ToArray();
            foreach ((ChannelId ChannelId, long EpisodeId) key in receive.Keys
                         .Where(key => key.ChannelId == channelId)
                         .ToArray())
            {
                receive.Remove(key);
                encryption.Remove(key);
            }
            transmit.Remove(channelId);
            foreach (ActiveRecording recording in recordings)
                await FinalizeAsync(recording, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ActiveRecording[] recordings = receive.Values
                .Concat(transmit.Values)
                .Distinct()
                .ToArray();
            receive.Clear();
            transmit.Clear();
            encryption.Clear();
            foreach (ActiveRecording recording in recordings)
                await FinalizeAsync(recording, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async ValueTask StopAsync(
        (ChannelId ChannelId, long EpisodeId) key,
        CancellationToken cancellationToken)
    {
        if (key.EpisodeId <= 0)
            return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (receive.Remove(key, out ActiveRecording? recording))
                await FinalizeAsync(recording, cancellationToken).ConfigureAwait(false);
            encryption.Remove(key);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ActiveRecording> CreateAsync(
        ChannelRecordingDescriptor channel,
        CallId callId,
        bool isTransmit,
        uint streamId,
        uint? sourceId,
        long? receiveEpisodeId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = clock.UtcNow;
        IRecordingWriteHandle handle = await store.CreateAsync(
            callId,
            channel.Id,
            startedAt,
            WaveMediaType,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = new PcmWavFileWriter(
                handle.Stream,
                PcmAudioFormat.Voice8KhzMono16Bit,
                leaveOpen: true);
            var recording = new ActiveRecording(
                channel,
                callId,
                handle,
                writer,
                startedAt,
                isTransmit,
                streamId,
                sourceId,
                sourceId is uint callerId && !isTransmit
                    ? resolveSubscriberAlias(channel.Id, callerId)
                    : string.Empty,
                receiveEpisodeId,
                CreateInitialEncryption(
                    channel,
                    isTransmit,
                    receiveEpisodeId ?? (isTransmit ? null : streamId)));
            recording.UpdateContext(startedAt, retentionDays);
            Publish(recording, isFinalizing: false, fault: null);
            return recording;
        }
        catch (Exception exception)
        {
            await handle.AbortAsync(exception.Message, CancellationToken.None).ConfigureAwait(false);
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Write(ActiveRecording recording, ReadOnlySpan<short> samples)
    {
        try
        {
            recording.Writer.Write(samples);
        }
        catch (Exception exception)
        {
            Publish(recording, isFinalizing: false, exception.Message);
            throw;
        }
    }

    private void TryUpdateContext(ActiveRecording recording)
    {
        try
        {
            recording.UpdateContext(clock.UtcNow, retentionDays);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
            // A failed metadata checkpoint must not interrupt otherwise valid
            // audio capture. The last durable snapshot remains recoverable.
            Publish(recording, isFinalizing: false, exception.Message);
        }
    }

    private async ValueTask FinalizeAsync(
        ActiveRecording recording,
        CancellationToken cancellationToken)
    {
        Publish(recording, isFinalizing: true, fault: null);
        Exception? failure = null;
        try
        {
            recording.Writer.Dispose();
            TimeSpan duration = TimeSpan.FromSeconds(
                recording.Writer.SamplesWritten /
                (double)PcmAudioFormat.Voice8KhzMono16Bit.SampleRate);
            await recording.Handle.CommitAsync(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            try
            {
                await recording.Handle.AbortAsync(
                    exception.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original finalization failure.
            }
            throw;
        }
        finally
        {
            await recording.Handle.DisposeAsync().ConfigureAwait(false);
            Publish(recording, isFinalizing: false, failure?.Message);
        }
    }

    private void Publish(
        ActiveRecording recording,
        bool isFinalizing,
        string? fault)
    {
        var state = new RecordingOperationState(
            recording.ChannelId,
            recording.CallId,
            recording.Handle.Id,
            recording.IsTransmit,
            isFinalizing,
            fault);
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch
        {
            // Observers cannot interrupt recording lifecycle transitions.
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private RecordingEncryptionDescriptor CreateInitialEncryption(
        ChannelRecordingDescriptor channel,
        bool isTransmit,
        long? receiveEpisodeId)
    {
        if (!isTransmit)
        {
            if (receiveEpisodeId is long episodeId &&
                encryption.TryGetValue((channel.Id, episodeId), out ReceiveEncryptionState? state) &&
                state.Encryption.IsKnown)
            {
                return state.Encryption;
            }
            return channel.Definition.Protocol == DvmConsole.Core.Runtime.ChannelProtocol.Analog
                ? RecordingEncryptionDescriptor.Clear
                : RecordingEncryptionDescriptor.Unknown;
        }

        bool secure = channel.Definition.IsEncrypted && channel.TransmitEncrypted;
        if (!secure)
            return RecordingEncryptionDescriptor.Clear;
        return TryParseConfiguredEncryption(
            channel.Definition,
            out byte algorithmId,
            out ushort keyId)
                ? RecordingEncryptionDescriptor.Secure(algorithmId, keyId)
                : RecordingEncryptionDescriptor.Secure(null, null);
    }

    private static bool TryParseConfiguredEncryption(
        DvmConsole.Core.Runtime.ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        algorithmId = 0;
        keyId = 0;
        return definition.Protocol switch
        {
            DvmConsole.Core.Runtime.ChannelProtocol.P25 =>
                P25KeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) &&
                P25KeyRing.TryParseKeyId(definition.EncryptionKeyId, out keyId),
            DvmConsole.Core.Runtime.ChannelProtocol.Dmr =>
                TryParseDmrEncryption(definition, out algorithmId, out keyId),
            DvmConsole.Core.Runtime.ChannelProtocol.Nxdn =>
                TryParseNxdnEncryption(definition, out algorithmId, out keyId),
            _ => false
        };
    }

    private static bool TryParseDmrEncryption(
        DvmConsole.Core.Runtime.ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        keyId = 0;
        if (!DmrKeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) ||
            !DmrKeyRing.TryParseKeyId(definition.EncryptionKeyId, out byte parsedKeyId))
        {
            return false;
        }
        keyId = parsedKeyId;
        return true;
    }

    private static bool TryParseNxdnEncryption(
        DvmConsole.Core.Runtime.ChannelRuntimeDefinition definition,
        out byte algorithmId,
        out ushort keyId)
    {
        keyId = 0;
        if (!NxdnKeyRing.TryParseAlgorithmId(definition.EncryptionAlgorithm, out algorithmId) ||
            !NxdnKeyRing.TryParseKeyId(definition.EncryptionKeyId, out byte parsedKeyId))
        {
            return false;
        }
        keyId = parsedKeyId;
        return true;
    }

    private sealed class ActiveRecording
    {
        private readonly Queue<uint> replacementStreamOrder = [];

        public ActiveRecording(
            ChannelRecordingDescriptor channel,
            CallId callId,
            IRecordingWriteHandle handle,
            PcmWavFileWriter writer,
            DateTimeOffset startedAt,
            bool isTransmit,
            uint streamId,
            uint? sourceId,
            string subscriberAlias,
            long? receiveEpisodeId,
            RecordingEncryptionDescriptor encryption)
        {
            Channel = channel;
            CallId = callId;
            Handle = handle;
            Writer = writer;
            StartedAt = startedAt;
            IsTransmit = isTransmit;
            StreamId = streamId;
            SourceId = sourceId;
            SubscriberAlias = subscriberAlias;
            ReceiveEpisodeId = receiveEpisodeId;
            Encryption = encryption;
            StreamIds = streamId == 0 ? [] : [streamId];
        }

        public ChannelRecordingDescriptor Channel { get; }
        public ChannelId ChannelId => Channel.Id;
        public CallId CallId { get; }
        public IRecordingWriteHandle Handle { get; }
        public PcmWavFileWriter Writer { get; }
        public DateTimeOffset StartedAt { get; }
        public bool IsTransmit { get; }
        public uint StreamId { get; }
        public uint? SourceId { get; }
        public string SubscriberAlias { get; }
        public long? ReceiveEpisodeId { get; }
        public HashSet<uint> StreamIds { get; }
        public RecordingEncryptionDescriptor Encryption { get; private set; }

        public bool ObservePhysicalStream(uint streamId)
        {
            if (streamId == 0 || !StreamIds.Add(streamId))
                return false;
            if (streamId != StreamId)
                replacementStreamOrder.Enqueue(streamId);
            while (StreamIds.Count > DvmConsole.Operations.ReceiveStreamPolicy.DefaultMaximumTrackedStreams)
            {
                uint oldestReplacement = replacementStreamOrder.Dequeue();
                StreamIds.Remove(oldestReplacement);
            }
            return true;
        }

        public bool SetEncryption(RecordingEncryptionDescriptor value)
        {
            if (Encryption == value)
                return false;
            Encryption = value;
            return true;
        }

        public void UpdateContext(DateTimeOffset observedAt, int retentionDays)
            => Handle.UpdateContext(new RecordingCaptureContext(
                Channel.Definition,
                IsTransmit ? "TX" : "RX",
                IsTransmit ? "ConsoleTx" : "InboundRadio",
                StreamId,
                StreamIds
                    .OrderBy(streamId => streamId == StreamId ? 0 : 1)
                    .ThenBy(streamId => streamId)
                    .ToArray(),
                SourceId,
                SubscriberAlias,
                Encryption,
                retentionDays > 0 ? retentionDays : null,
                ReceiveEpisodeId,
                observedAt));
    }

    private sealed class ReceiveEncryptionState
    {
        private bool dmrPrivacyHeaderPending;
        public RecordingEncryptionDescriptor Encryption { get; private set; }

        public bool Observe(IRadioMediaFrame traffic)
        {
            RadioFrameEncryption? resolved = RadioFrameEncryptionResolver.TryResolve(traffic);
            if (resolved is RadioFrameEncryption explicitEncryption)
            {
                dmrPrivacyHeaderPending = false;
                return Apply(new RecordingEncryptionDescriptor(
                    IsKnown: true,
                    explicitEncryption.IsSecure,
                    explicitEncryption.IsSecure ? explicitEncryption.AlgorithmId : null,
                    explicitEncryption.IsSecure ? explicitEncryption.KeyId : null));
            }

            if (traffic.Protocol != DvmConsole.Core.Runtime.RadioMediaProtocol.Dmr)
                return false;
            if (RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic))
            {
                dmrPrivacyHeaderPending = true;
                return false;
            }
            if (dmrPrivacyHeaderPending && RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic))
            {
                dmrPrivacyHeaderPending = false;
                return Apply(RecordingEncryptionDescriptor.Clear);
            }
            if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
                dmrPrivacyHeaderPending = false;
            return false;
        }

        private bool Apply(RecordingEncryptionDescriptor value)
        {
            if (!value.IsKnown || Encryption == value)
                return false;
            Encryption = value;
            return true;
        }
    }
}
