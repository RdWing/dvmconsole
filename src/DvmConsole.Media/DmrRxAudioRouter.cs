using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

/// <summary>
/// Serializes selected DMR traffic into one playback path. FNE callbacks may
/// arrive concurrently with device callbacks, so the router never allows two
/// decoded packets to write to the same playback device at once.
/// </summary>
public sealed class DmrRxAudioRouter : IAsyncDisposable
{
    private readonly DmrTrafficSelector selector;
    private readonly DmrRxAudioSession session;
    private readonly SemaphoreSlim processing = new(1, 1);
    private bool disposed;

    public DmrRxAudioRouter(
        DmrTrafficSelector selector,
        IVocoderSession vocoder,
        IAudioPlayback playback)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        session = new DmrRxAudioSession(vocoder, playback);
    }

    public int FramesDecoded => session.FramesDecoded;

    public async ValueTask<int> ProcessAsync(
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;

        await processing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            processing.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        await processing.WaitAsync().ConfigureAwait(false);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            processing.Release();
            processing.Dispose();
        }
    }
}
