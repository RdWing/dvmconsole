using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

/// <summary>
/// Sends generated 8 kHz PCM tone sequences through the selected channel's
/// normal DMR, P25, or analog call lifecycle. It deliberately does not open a
/// microphone; generated audio is paced as 20 ms media frames instead.
/// </summary>
public sealed class ToneTransmitCoordinator : IAsyncDisposable
{
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private bool sending;

    public ToneTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY")));
    }

    public bool IsSending => sending;

    public async Task SendAsync(
        ChannelViewModel channel,
        SystemViewModel system,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(system);
        if (samples.IsEmpty)
            throw new ArgumentException("Tone audio cannot be empty.", nameof(samples));
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!channel.CanTransmit)
                throw new InvalidOperationException("The selected channel cannot transmit generated audio.");
            if (!system.IsConnected)
                throw new InvalidOperationException($"The FNE system '{system.Name}' is not connected.");
            if (system.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{system.Name}' has no valid transmit RID.");

            sending = true;
            await SendCoreAsync(channel, system, sourceId, samples, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sending = false;
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
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task SendCoreAsync(
        ChannelViewModel channel,
        SystemViewModel system,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken)
    {
        ChannelRuntimeDefinition definition = ChannelTransmitDefinitionFactory.Create(channel);
        P25TxEncryptionOptions? encryption = ChannelTransmitDefinitionFactory.CreateEncryptionOptions(
            channel,
            definition,
            p25KeyResolver);
        IVocoderBackend? vocoderBackend = null;
        IVocoderSession? vocoderSession = null;
        PatchTransmitSession? session = null;

        try
        {
            if (definition.Mode is "dmr" or "p25")
            {
                vocoderBackend = createVocoderBackend();
                vocoderSession = vocoderBackend.CreateSession(
                    definition.Mode == "dmr" ? VocoderMode.DmrAmbe : VocoderMode.P25Imbe);
            }

            uint streamId = system.CreateStreamId();
            session = new PatchTransmitSession(
                definition,
                sourceId,
                streamId,
                vocoderSession,
                (payload, sequence, stream) => system.SendTraffic(
                    ToProtocol(definition.Mode),
                    payload.Span,
                    sequence,
                    stream),
                encryption);
            vocoderSession = null;
            session.Start();
            channel.SetTransmitEnabled(true, streamId);

            try
            {
                for (int offset = 0; offset < samples.Length; offset += VocoderFrameSizes.PcmSamplesPerFrame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    short[] frame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                    int count = Math.Min(frame.Length, samples.Length - offset);
                    samples.Span.Slice(offset, count).CopyTo(frame);
                    session.Process(frame);
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
                }

                session.End();
            }
            finally
            {
                channel.SetTransmitEnabled(false);
            }
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    if (session.IsStarted && !session.IsEnded)
                        session.End();
                }
                finally
                {
                    session.Dispose();
                }
            }

            vocoderSession?.Dispose();
            vocoderBackend?.Dispose();
        }
    }

    private static FneTrafficProtocol ToProtocol(string mode)
        => mode switch
        {
            "dmr" => FneTrafficProtocol.Dmr,
            "p25" => FneTrafficProtocol.P25,
            "analog" => FneTrafficProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
}
