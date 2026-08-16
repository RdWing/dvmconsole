namespace DvmConsole.Audio;

// Cross-platform microphone processing settings. Device selection is resolved
// by the host; these values only describe the PCM processing applied after
// capture and before protocol encoding.
public sealed class AudioInputProcessingOptions
{
    public string DeviceId { get; init; } = "default";
    public bool AgcEnabled { get; init; }
    public double Gain { get; init; } = 1.0;
    public double LowGainDb { get; init; }
    public double MidGainDb { get; init; }
    public double HighGainDb { get; init; }

    public AudioInputProcessingOptions Normalize()
        => new()
        {
            DeviceId = string.IsNullOrWhiteSpace(DeviceId) ? "default" : DeviceId.Trim(),
            AgcEnabled = AgcEnabled,
            Gain = NormalizeFinite(Gain, 1.0, 0.25, 3.0),
            LowGainDb = NormalizeFinite(LowGainDb, 0, -12, 12),
            MidGainDb = NormalizeFinite(MidGainDb, 0, -12, 12),
            HighGainDb = NormalizeFinite(HighGainDb, 0, -12, 12)
        };

    private static double NormalizeFinite(double value, double fallback, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

// Applies bounded microphone gain, optional three-band shaping, and optional
// block AGC without coupling the capture path to a platform audio API.
public sealed class PcmInputProcessor
{
    private const double AgcTargetRms = 0.18;
    private readonly AudioInputProcessingOptions options;
    private readonly double lowGain;
    private readonly double midGain;
    private readonly double highGain;
    private double lowState;
    private double highState;
    private double previousInput;
    private double agcGain = 1.0;

    public PcmInputProcessor(AudioInputProcessingOptions? options = null)
    {
        this.options = (options ?? new AudioInputProcessingOptions()).Normalize();
        lowGain = DbToLinear(this.options.LowGainDb);
        midGain = DbToLinear(this.options.MidGainDb);
        highGain = DbToLinear(this.options.HighGainDb);
    }

    public void Process(ReadOnlySpan<short> input, Span<short> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output buffer is smaller than the input buffer.", nameof(output));
        if (input.IsEmpty)
            return;

        Span<double> shaped = input.Length <= 2048
            ? stackalloc double[input.Length]
            : new double[input.Length];
        double sumSquares = 0;
        for (int index = 0; index < input.Length; index++)
        {
            double sample = input[index] / (double)short.MaxValue;
            lowState += 0.12 * (sample - lowState);
            double high = 0.08 * (highState + sample - previousInput);
            highState = high;
            previousInput = sample;
            double mid = sample - lowState - high;
            double value = lowState * lowGain + mid * midGain + high * highGain;
            shaped[index] = value;
            sumSquares += value * value;
        }

        if (options.AgcEnabled)
        {
            double rms = Math.Sqrt(sumSquares / input.Length);
            double requestedGain = AgcTargetRms / Math.Max(rms, 0.001);
            requestedGain = Math.Clamp(requestedGain, 0.25, 3.0);
            agcGain = (agcGain * 0.8) + (requestedGain * 0.2);
        }
        else
        {
            agcGain = 1.0;
        }

        double totalGain = options.Gain * agcGain;
        for (int index = 0; index < input.Length; index++)
        {
            output[index] = (short)Math.Clamp(
                Math.Round(shaped[index] * totalGain * short.MaxValue, MidpointRounding.AwayFromZero),
                short.MinValue,
                short.MaxValue);
        }
    }

    private static double DbToLinear(double decibels)
        => Math.Pow(10, decibels / 20.0);
}

// Decorates a platform capture with the shared PCM microphone processor while
// preserving the existing capture lifecycle contract.
public sealed class ProcessedAudioCapture : IAudioCapture
{
    private readonly IAudioCapture source;
    private readonly PcmInputProcessor processor;
    private bool disposed;

    public ProcessedAudioCapture(IAudioCapture source, AudioInputProcessingOptions? options = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        processor = new PcmInputProcessor(options);
        Format = source.Format;
        source.SamplesAvailable += HandleSamplesAvailable;
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public PcmAudioFormat Format { get; }
    public bool IsRunning => source.IsRunning;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => source.StartAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
        => source.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        source.SamplesAvailable -= HandleSamplesAvailable;
        await source.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }

    private void HandleSamplesAvailable(object? sender, PcmSamplesEventArgs args)
    {
        short[] processed = new short[args.Samples.Length];
        processor.Process(args.Samples.Span, processed);
        SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(processed));
    }
}
