using DvmConsole.Audio;

namespace DvmConsole.Media;

// Mixes PCM from independently selected receive channels into one playback
// stream. The mixer emits one 20 ms frame at a time and treats channels with
// no frame ready as silence, so a quiet channel cannot block an active one.
public sealed class AudioMixer : IAsyncDisposable
{
    private const int MaximumBufferedFrames = 12;
    private const int TargetOutputBufferedFrames = 4;
    private readonly IAudioPlayback output;
    private readonly PcmAudioFormat inputFormat;
    private readonly object sync = new();
    private readonly Dictionary<int, ChannelBuffer> channels = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pump;
    private readonly int frameSamples;
    private readonly int outputFrameSamples;
    private readonly int targetOutputBufferedSamples;
    private readonly int maximumBufferedSamples;
    private int nextChannelId;
    private bool disposed;
    private Exception? failure;
    private long droppedSamples;
    private long protectedFrames;

    public AudioMixer(IAudioPlayback output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (output.Format.Channels is not (1 or 2) || output.Format.BitsPerSample != 16)
            throw new NotSupportedException("Audio mixing supports mono or stereo 16-bit PCM output only.");

        frameSamples = Math.Max(1, output.Format.SampleRate / 50);
        outputFrameSamples = checked(frameSamples * output.Format.Channels);
        targetOutputBufferedSamples = checked(outputFrameSamples * TargetOutputBufferedFrames);
        inputFormat = new PcmAudioFormat(output.Format.SampleRate, 1, output.Format.BitsPerSample);
        maximumBufferedSamples = checked(frameSamples * MaximumBufferedFrames);
        pump = PumpAsync(cancellation.Token);
    }

    public PcmAudioFormat Format => inputFormat;

    public int MaximumBufferedSamples => maximumBufferedSamples;

    public long DroppedSamples
    {
        get
        {
            lock (sync)
                return droppedSamples;
        }
    }

    public long ProtectedFrames
    {
        get
        {
            lock (sync)
                return protectedFrames;
        }
    }

    public IAudioPlayback OpenChannel()
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            var channel = new ChannelBuffer(++nextChannelId, frameSamples);
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
                int framesToWrite = FramesNeededForOutputBuffer();
                for (int index = 0; index < framesToWrite; index++)
                {
                    short[]? frame = TakeFrame();
                    if (frame is null)
                        break;
                    await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
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

    private int FramesNeededForOutputBuffer()
    {
        if (output.QueuedSamples is not int queuedSamples)
            return 1;

        // PeriodicTimer coalesces missed ticks. Use the device's actual queue
        // depth to refill several already-decoded frames after a delayed wake,
        // instead of remaining permanently behind by all of the missed ticks.
        int deficit = targetOutputBufferedSamples - Math.Max(0, queuedSamples);
        if (deficit <= 0)
            return 0;
        return Math.Min(
            TargetOutputBufferedFrames,
            (deficit + outputFrameSamples - 1) / outputFrameSamples);
    }

    private short[]? TakeFrame()
    {
        lock (sync)
        {
            if (channels.Count == 0 || channels.Values.All(channel => channel.Frames.Count == 0))
                return null;

            var left = new double[frameSamples];
            double[]? right = output.Format.Channels == 2 ? new double[frameSamples] : null;
            foreach (ChannelBuffer channel in channels.Values)
            {
                if (!channel.Frames.TryDequeue(out short[]? source))
                    continue;

                int count = Math.Min(frameSamples, source.Length);
                double leftBalance = right is null || channel.Balance <= 0 ? 1.0 : 1.0 - channel.Balance;
                double rightBalance = channel.Balance >= 0 ? 1.0 : 1.0 + channel.Balance;
                for (int index = 0; index < count; index++)
                {
                    double gained = source[index] * channel.Gain;
                    left[index] += gained * leftBalance;
                    if (right is not null)
                        right[index] += gained * rightBalance;
                }
            }

            double peak = left.Select(Math.Abs).DefaultIfEmpty().Max();
            if (right is not null)
                peak = Math.Max(peak, right.Select(Math.Abs).DefaultIfEmpty().Max());
            double protection = peak > short.MaxValue ? short.MaxValue / peak : 1.0;
            if (protection < 1.0)
                protectedFrames++;

            var frame = new short[checked(frameSamples * output.Format.Channels)];
            for (int index = 0; index < frameSamples; index++)
            {
                int outputIndex = index * output.Format.Channels;
                frame[outputIndex] = ToPcm(left[index] * protection);
                if (right is not null)
                    frame[outputIndex + 1] = ToPcm(right[index] * protection);
            }
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

            ReadOnlySpan<short> incoming = samples.Span;
            while (!incoming.IsEmpty)
            {
                int count = Math.Min(frameSamples - channel.PartialCount, incoming.Length);
                incoming[..count].CopyTo(channel.PartialFrame.AsSpan(channel.PartialCount));
                channel.PartialCount += count;
                incoming = incoming[count..];
                if (channel.PartialCount < frameSamples)
                    continue;

                while (channel.Frames.Count >= MaximumBufferedFrames && channel.Frames.TryDequeue(out short[]? discarded))
                {
                    channel.DroppedSamples += discarded.Length;
                    droppedSamples += discarded.Length;
                }

                channel.Frames.Enqueue(channel.PartialFrame);
                channel.PartialFrame = new short[frameSamples];
                channel.PartialCount = 0;
            }
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

    private static short ToPcm(double sample)
        => (short)Math.Clamp(
            Math.Round(sample, MidpointRounding.AwayFromZero),
            short.MinValue,
            short.MaxValue);

    private sealed class ChannelBuffer(int id, int frameSamples)
    {
        public int Id { get; } = id;
        public Queue<short[]> Frames { get; } = [];
        public short[] PartialFrame { get; set; } = new short[frameSamples];
        public int PartialCount { get; set; }
        public double Gain { get; set; } = 1.0;
        public double Balance { get; set; }
        public int DroppedSamples { get; set; }
        public bool Disposed { get; set; }
    }

    private sealed class ChannelPlayback(AudioMixer owner, ChannelBuffer channel) : IAudioPlayback, IAudioGainControl, IAudioBalanceControl
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

        public double Balance
        {
            get
            {
                lock (owner.sync)
                    return channel.Balance;
            }
            set
            {
                if (!double.IsFinite(value) || value is < -1 or > 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "Audio balance must be between -1 and 1.");
                lock (owner.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    owner.ThrowIfUnavailable();
                    if (channel.Disposed || !owner.channels.ContainsKey(channel.Id))
                        throw new ObjectDisposedException(nameof(IAudioPlayback));
                    channel.Balance = value;
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
