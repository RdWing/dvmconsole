using DvmConsole.Audio;

namespace DvmConsole.Media;

/// <summary>
/// Mixes PCM from independently selected receive channels into one playback
/// stream. The mixer emits one 20 ms frame at a time and treats channels with
/// no frame ready as silence, so a quiet channel cannot block an active one.
/// </summary>
public sealed class AudioMixer : IAsyncDisposable
{
    private readonly IAudioPlayback output;
    private readonly object sync = new();
    private readonly Dictionary<int, ChannelBuffer> channels = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pump;
    private readonly int frameSamples;
    private int nextChannelId;
    private bool disposed;
    private Exception? failure;

    public AudioMixer(IAudioPlayback output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (output.Format.Channels != 1 || output.Format.BitsPerSample != 16)
            throw new NotSupportedException("Audio mixing currently supports mono 16-bit PCM only.");

        frameSamples = Math.Max(1, output.Format.SampleRate / 50);
        pump = PumpAsync(cancellation.Token);
    }

    public PcmAudioFormat Format => output.Format;

    public IAudioPlayback OpenChannel()
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            var channel = new ChannelBuffer(++nextChannelId);
            channels.Add(channel.Id, channel);
            return new ChannelPlayback(this, channel);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            foreach (ChannelBuffer channel in channels.Values)
                channel.Disposed = true;
            channels.Clear();
            cancellation.Cancel();
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            await output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds((double)frameSamples / output.Format.SampleRate));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                short[]? frame = TakeFrame();
                if (frame is not null)
                    await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during mixer shutdown.
        }
        catch (Exception exception)
        {
            lock (sync)
                failure = exception;
        }
    }

    private short[]? TakeFrame()
    {
        lock (sync)
        {
            if (channels.Count == 0 || channels.Values.All(channel => channel.Samples.Count == 0))
                return null;

            var mixed = new int[frameSamples];
            foreach (ChannelBuffer channel in channels.Values)
            {
                int count = Math.Min(frameSamples, channel.Samples.Count);
                for (int index = 0; index < count; index++)
                    mixed[index] += (int)Math.Round(
                        channel.Samples.Dequeue() * channel.Gain,
                        MidpointRounding.AwayFromZero);
            }

            var frame = new short[frameSamples];
            for (int index = 0; index < frame.Length; index++)
                frame[index] = Saturate(mixed[index]);
            return frame;
        }
    }

    private void Enqueue(ChannelBuffer channel, ReadOnlyMemory<short> samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.IsEmpty)
            return;

        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));

            foreach (short sample in samples.Span)
                channel.Samples.Enqueue(sample);
        }
    }

    private void Remove(ChannelBuffer channel)
    {
        lock (sync)
        {
            if (channel.Disposed)
                return;
            channel.Disposed = true;
            channels.Remove(channel.Id);
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure is not null)
            throw new IOException("The shared audio mixer stopped.", failure);
    }

    private static short Saturate(int sample)
    {
        return sample switch
        {
            > short.MaxValue => short.MaxValue,
            < short.MinValue => short.MinValue,
            _ => (short)sample
        };
    }

    private sealed class ChannelBuffer(int id)
    {
        public int Id { get; } = id;
        public Queue<short> Samples { get; } = [];
        public double Gain { get; set; } = 1.0;
        public bool Disposed { get; set; }
    }

    private sealed class ChannelPlayback(AudioMixer owner, ChannelBuffer channel) : IAudioPlayback, IAudioGainControl
    {
        private bool disposed;

        public PcmAudioFormat Format => owner.Format;

        public double Gain
        {
            get
            {
                lock (owner.sync)
                    return channel.Gain;
            }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 4)
                    throw new ArgumentOutOfRangeException(nameof(value), "Audio gain must be between 0 and 4.");
                lock (owner.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    owner.ThrowIfUnavailable();
                    if (channel.Disposed || !owner.channels.ContainsKey(channel.Id))
                        throw new ObjectDisposedException(nameof(IAudioPlayback));
                    channel.Gain = value;
                }
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Enqueue(channel, samples, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                owner.Remove(channel);
            }

            return ValueTask.CompletedTask;
        }
    }
}
