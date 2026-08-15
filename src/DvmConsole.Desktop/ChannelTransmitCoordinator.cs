using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

/// <summary>
/// Lazily owns one explicit transmit capture path. No input device, vocoder, or
/// network traffic is opened until the operator presses PTT. Analog calls use
/// the dvmhost-compatible μ-law packetizer and intentionally do not allocate a
/// vocoder session.
/// </summary>
public sealed class ChannelTransmitCoordinator : IAsyncDisposable
{
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAudioBackend? audioBackend;
    private IVocoderBackend? vocoderBackend;
    private ITransmitCaptureSession? session;
    private ChannelViewModel? activeChannel;
    private uint activeStreamId;
    private bool disposed;
    private AudioInputProcessingOptions audioInputOptions;

    public event EventHandler<Exception>? Faulted;
    public ChannelViewModel? ActiveChannel => activeChannel;
    public uint ActiveStreamId => activeStreamId;

    public ChannelTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        AudioInputProcessingOptions? audioInputOptions = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.audioInputOptions = (audioInputOptions ?? new AudioInputProcessingOptions()).Normalize();
    }

    public void UpdateAudioInputOptions(AudioInputProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        audioInputOptions = options.Normalize();
    }

    public async Task StartAsync(ChannelViewModel channel, SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(system);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!channel.CanTransmit)
                throw new InvalidOperationException("Only clear, non-RX-only DMR/P25/analog channels can transmit in this slice.");
            if (!system.IsConnected)
                throw new InvalidOperationException($"The FNE system '{system.Name}' is not connected.");
            if (system.SourceId is not uint sourceId)
                throw new InvalidOperationException($"The FNE system '{system.Name}' has no valid transmit RID.");

            await StopCoreAsync().ConfigureAwait(false);

            IAudioBackend? createdAudioBackend = null;
            IVocoderBackend? createdVocoderBackend = null;
            ITransmitCaptureSession? createdSession = null;
            try
            {
                createdAudioBackend = AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));
                AudioDeviceInfo input = SelectInput(
                    createdAudioBackend.EnumerateDevices(AudioDirection.Input),
                    audioInputOptions.DeviceId);
                IAudioCapture capture = new ProcessedAudioCapture(
                    createdAudioBackend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit),
                    audioInputOptions);

                bool isDmr = channel.Definition.Mode == "dmr";
                bool isAnalog = channel.Definition.Mode == "analog";
                P25TxEncryptionOptions? encryption = !isDmr && !isAnalog
                    ? CreateP25EncryptionOptions(channel)
                    : null;
                if (!isAnalog)
                    createdVocoderBackend = new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY"));

                uint streamId = system.CreateStreamId();
                Action<ReadOnlyMemory<byte>, ushort, uint> send = (payload, sequence, stream) => system.SendTraffic(
                    isDmr
                        ? FneTrafficProtocol.Dmr
                        : isAnalog
                            ? FneTrafficProtocol.Analog
                            : FneTrafficProtocol.P25,
                    payload.Span,
                    sequence,
                    stream);
                if (isAnalog)
                {
                    createdSession = new AnalogTransmitCaptureSession(
                        capture,
                        sourceId,
                        channel.Definition.DestinationId,
                        streamId,
                        send);
                }
                else
                {
                    if (createdVocoderBackend is null)
                        throw new InvalidOperationException("A vocoder backend is required for digital transmit.");

                    IVocoderSession vocoder = createdVocoderBackend.CreateSession(
                        isDmr ? VocoderMode.DmrAmbe : VocoderMode.P25Imbe);
                    createdSession = isDmr
                        ? new DmrTransmitCaptureSession(
                            capture,
                            vocoder,
                            sourceId,
                            channel.Definition.DestinationId,
                            channel.Definition.Slot,
                            streamId,
                            send)
                        : new P25TransmitCaptureSession(
                            capture,
                            vocoder,
                            sourceId,
                            channel.Definition.DestinationId,
                            streamId,
                            send,
                            encryption);
                }
                createdSession.Faulted += HandleSessionFaulted;

                await createdSession.StartAsync().ConfigureAwait(false);
                audioBackend = createdAudioBackend;
                vocoderBackend = createdVocoderBackend;
                session = createdSession;
                activeChannel = channel;
                activeStreamId = streamId;
            }
            catch
            {
                if (createdSession is not null)
                {
                    createdSession.Faulted -= HandleSessionFaulted;
                    await createdSession.DisposeAsync().ConfigureAwait(false);
                }

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

    private async Task StopCoreAsync()
    {
        ITransmitCaptureSession? currentSession = session;
        session = null;
        activeChannel = null;
        activeStreamId = 0;

        Exception? failure = null;
        if (currentSession is not null)
        {
            currentSession.Faulted -= HandleSessionFaulted;
            try
            {
                await currentSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        vocoderBackend?.Dispose();
        vocoderBackend = null;
        audioBackend?.Dispose();
        audioBackend = null;

        if (failure is not null)
            throw failure;
    }

    private void HandleSessionFaulted(object? sender, Exception exception)
    {
        Faulted?.Invoke(this, exception);
    }

    private static AudioDeviceInfo SelectInput(IReadOnlyList<AudioDeviceInfo> devices, string deviceId)
    {
        return devices.FirstOrDefault(device => device.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException("No audio input device is available.");
    }

    private P25TxEncryptionOptions? CreateP25EncryptionOptions(ChannelViewModel channel)
    {
        if (!channel.Definition.IsEncrypted || !channel.IsTransmitEncrypted)
            return null;
        if (p25KeyResolver is null ||
            !P25KeyRing.TryParseAlgorithmId(channel.Definition.EncryptionAlgorithm, out byte algorithmId) ||
            !P25KeyRing.TryParseKeyId(channel.Definition.EncryptionKeyId, out ushort keyId) ||
            !p25KeyResolver.TryResolve(algorithmId, keyId, out ReadOnlyMemory<byte> key))
        {
            throw new NotSupportedException(
                $"P25 encrypted transmit requires a configured key for {channel.Definition.EncryptionAlgorithm}/{channel.Definition.EncryptionKeyId}.");
        }

        return P25TxEncryptionOptions.CreateRandom(algorithmId, keyId, key);
    }
}
