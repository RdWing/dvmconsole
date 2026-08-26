using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Composes the existing protocol selector, vocoder, and playback boundaries
// for one configured channel. The caller owns the lifecycle decision; this
// class never opens an audio device or native library by itself.
public sealed class ChannelReceiveAudioSession : IAsyncDisposable
{
    private readonly IAudioPlayback playback;
    private readonly DmrRxAudioRouter? dmrRouter;
    private readonly P25RxAudioSession? p25Session;
    private readonly NxdnRxAudioSession? nxdnSession;
    private readonly AnalogRxAudioSession? analogSession;
    private bool disposed;

    public ChannelReceiveAudioSession(
        ChannelRuntimeDefinition definition,
        IVocoderSession? vocoder,
        IAudioPlayback playback,
        IP25KeyResolver? keyResolver = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        IReceivePrivacyPolicy? privacyPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(playback);

        Definition = definition;
        this.playback = playback;
        bool hasFixedPrivacy = HasFixedPrivacy(definition);
        switch (definition.Mode)
        {
            case "dmr":
                ArgumentNullException.ThrowIfNull(vocoder);
                dmrRouter = new DmrRxAudioRouter(
                    new DmrTrafficSelector(definition.DestinationId, definition.Slot),
                    vocoder,
                    playback,
                    dmrKeyResolver,
                    definition.SystemName,
                    hasFixedPrivacy,
                    privacyMayVary: definition.SelectableEncryption,
                    privacyPolicy);
                break;
            case "p25":
                ArgumentNullException.ThrowIfNull(vocoder);
                p25Session = new P25RxAudioSession(
                    new P25TrafficSelector(definition.DestinationId),
                    vocoder,
                    playback,
                    keyResolver,
                    definition.SystemName,
                    privacyPolicy);
                break;
            case "nxdn":
                ArgumentNullException.ThrowIfNull(vocoder);
                nxdnSession = new NxdnRxAudioSession(
                    new NxdnTrafficSelector(definition.DestinationId),
                    vocoder,
                    playback,
                    nxdnKeyResolver,
                    definition.SystemName,
                    hasFixedPrivacy ? definition.EncryptionAlgorithm : null,
                    hasFixedPrivacy ? definition.EncryptionKeyId : null,
                    privacyPolicy);
                break;
            case "analog":
                analogSession = new AnalogRxAudioSession(
                    new AnalogTrafficSelector(definition.DestinationId),
                    playback);
                break;
            default:
                throw new ArgumentException($"Unsupported receive audio mode '{definition.Mode}'.", nameof(definition));
        }
    }

    public ChannelRuntimeDefinition Definition { get; }

    private static bool HasFixedPrivacy(ChannelRuntimeDefinition definition)
        => definition.IsEncrypted && !definition.SelectableEncryption;

    public int FramesDecoded => dmrRouter?.FramesDecoded ?? p25Session?.FramesDecoded ?? nxdnSession?.FramesDecoded ?? analogSession?.FramesDecoded ?? 0;

    public long MalformedPackets
        => dmrRouter?.MalformedPackets ?? p25Session?.MalformedPackets ?? nxdnSession?.MalformedPackets ?? analogSession?.MalformedPackets ?? 0;

    public long LostPackets => dmrRouter?.LostPackets ?? p25Session?.LostPackets ?? nxdnSession?.LostPackets ?? 0;

    public long DuplicateOrLatePackets => dmrRouter?.DuplicateOrLatePackets ?? p25Session?.DuplicateOrLatePackets ?? nxdnSession?.DuplicateOrLatePackets ?? 0;

    public ReceiveAudioDiagnostics GetDiagnostics()
        => new(FramesDecoded, LostPackets, DuplicateOrLatePackets, MalformedPackets);

    public void SetGain(double gain)
    {
        if (playback is IAudioGainControl gainControl)
            gainControl.Gain = gain;
    }

    public void SetBalance(double balance)
    {
        if (playback is IAudioBalanceControl balanceControl)
            balanceControl.Balance = balance;
    }

    public ValueTask<int> ProcessAsync(
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);

        if (dmrRouter is not null)
            return dmrRouter.ProcessAsync(traffic, cancellationToken);
        if (p25Session is not null)
            return p25Session.ProcessAsync(traffic, cancellationToken);
        if (nxdnSession is not null)
            return nxdnSession.ProcessAsync(traffic, cancellationToken);
        return analogSession!.ProcessAsync(traffic, cancellationToken);
    }

    // Marks decoder input complete without closing the shared output route.
    // Mixer-backed playback uses this to release short calls that end before
    // reaching the normal startup cushion and to drain their queued frames.
    public ValueTask CompletePlaybackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return playback.FlushAsync(cancellationToken);
    }

    public void SetLivePlaybackEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (playback is not ILiveAudioPlaybackControl playbackControl)
        {
            throw new NotSupportedException(
                "This receive playback path does not support independent live presentation.");
        }
        playbackControl.LivePlaybackEnabled = enabled;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        if (dmrRouter is not null)
            await dmrRouter.DisposeAsync().ConfigureAwait(false);
        else if (p25Session is not null)
            await p25Session.DisposeAsync().ConfigureAwait(false);
        else if (nxdnSession is not null)
            await nxdnSession.DisposeAsync().ConfigureAwait(false);
        else if (analogSession is not null)
            await analogSession.DisposeAsync().ConfigureAwait(false);

        disposed = true;
    }
}
