using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal readonly record struct EpisodeLivePlayoutDiagnostics(
    long ProducerHandoffs,
    long SuppressedRetiredSamples)
{
    public static EpisodeLivePlayoutDiagnostics operator +(
        EpisodeLivePlayoutDiagnostics left,
        EpisodeLivePlayoutDiagnostics right)
        => new(
            checked(left.ProducerHandoffs + right.ProducerHandoffs),
            checked(left.SuppressedRetiredSamples + right.SuppressedRetiredSamples));
}

// Selects one live producer for a logical call while preserving independent
// physical decoders upstream. A newer stream takes ownership only when it has
// decoded PCM ready, so a header-only replacement cannot silence a healthy
// predecessor. Once ownership advances, delayed PCM from retired streams is
// intentionally excluded from live playback; TAR has already observed it.
internal sealed class EpisodeLivePlayoutArbiter
{
    private readonly object sync = new();
    private readonly IAudioPlayback playback;
    private long nextGeneration;
    private long highestActivatedGeneration;
    private Producer? activeProducer;
    private long producerHandoffs;
    private long suppressedRetiredSamples;
    private bool completed;

    public EpisodeLivePlayoutArbiter(IAudioPlayback playback)
        => this.playback = playback ?? throw new ArgumentNullException(nameof(playback));

    public Producer Register(uint physicalStreamId)
    {
        if (physicalStreamId == 0)
            throw new ArgumentOutOfRangeException(nameof(physicalStreamId));

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            return new Producer(this, physicalStreamId, checked(++nextGeneration));
        }
    }

    public EpisodeLivePlayoutDiagnostics GetDiagnostics()
    {
        lock (sync)
            return new EpisodeLivePlayoutDiagnostics(producerHandoffs, suppressedRetiredSamples);
    }

    public void Complete()
    {
        lock (sync)
        {
            completed = true;
            activeProducer = null;
        }
    }

    private ValueTask WriteAsync(
        Producer producer,
        ReadOnlyMemory<short> samples,
        LiveWriteKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.IsEmpty)
            return ValueTask.CompletedTask;

        bool accepted;
        bool boundaryChanged = false;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            ObjectDisposedException.ThrowIf(producer.Released, producer);

            if (producer.Generation < highestActivatedGeneration)
            {
                suppressedRetiredSamples = checked(
                    suppressedRetiredSamples + samples.Length);
                accepted = false;
            }
            else
            {
                accepted = true;
                if (!ReferenceEquals(activeProducer, producer))
                {
                    if (activeProducer is not null)
                        producerHandoffs = checked(producerHandoffs + 1);
                    boundaryChanged = highestActivatedGeneration > 0;
                    activeProducer = producer;
                    highestActivatedGeneration = producer.Generation;
                }
            }
        }

        if (!accepted)
            return ValueTask.CompletedTask;
        if (boundaryChanged && playback is IAudioPlaybackBoundaryControl boundary)
            boundary.MarkInputBoundary();

        return kind switch
        {
            LiveWriteKind.Packet when playback is ILivePacketAudioPlayback packet =>
                packet.WriteLivePacketAsync(samples, cancellationToken),
            LiveWriteKind.Concealment when playback is IConcealmentAudioPlayback concealment =>
                concealment.WriteConcealmentAsync(samples, cancellationToken),
            _ => playback.WriteAsync(samples, cancellationToken)
        };
    }

    private void Release(Producer producer)
    {
        lock (sync)
        {
            if (producer.Released)
                return;
            producer.Released = true;
            if (ReferenceEquals(activeProducer, producer))
                activeProducer = null;
        }
    }

    private enum LiveWriteKind
    {
        Ordinary,
        Packet,
        Concealment
    }

    internal sealed class Producer(
        EpisodeLivePlayoutArbiter owner,
        uint physicalStreamId,
        long generation)
    {
        public uint PhysicalStreamId { get; } = physicalStreamId;
        public long Generation { get; } = generation;
        public bool Released { get; internal set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => owner.WriteAsync(this, samples, LiveWriteKind.Ordinary, cancellationToken);

        public ValueTask WritePacketAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => owner.WriteAsync(this, samples, LiveWriteKind.Packet, cancellationToken);

        public ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => owner.WriteAsync(this, samples, LiveWriteKind.Concealment, cancellationToken);

        public void Release() => owner.Release(this);
    }
}
