namespace DvmConsole.Vocoder;

// User-configurable portions of the built-in vocoder's receive enhancement
// chain. Output gain and frame-boundary smoothing are intentionally fixed in
// the native adapter and are not represented here.
public sealed record ReceiveAudioProcessingOptions
{
    public bool HighPassFilterEnabled { get; init; } = true;
    public float HighPassFrequencyHz { get; init; } = 250.0f;
    public bool PeakingFilterEnabled { get; init; } = true;
    public float PeakingFrequencyHz { get; init; } = 2_500.0f;
    public float PeakingGainDb { get; init; } = 3.0f;
    public bool CompressorEnabled { get; init; }
    public float CompressorRatio { get; init; } = 3.0f;
    public float CompressorThresholdDbfs { get; init; } = -18.0f;
    public float CompressorMakeupGainDb { get; init; } = 3.0f;

    internal void Validate()
    {
        ValidateRange(HighPassFrequencyHz, 0, 500, nameof(HighPassFrequencyHz));
        ValidateRange(PeakingFrequencyHz, 250, 3_000, nameof(PeakingFrequencyHz));
        ValidateRange(PeakingGainDb, -10, 10, nameof(PeakingGainDb));
        ValidateRange(CompressorRatio, 1, 10, nameof(CompressorRatio));
        ValidateRange(CompressorThresholdDbfs, -40, 0, nameof(CompressorThresholdDbfs));
        ValidateRange(CompressorMakeupGainDb, 0, 10, nameof(CompressorMakeupGainDb));
    }

    private static void ValidateRange(float value, float minimum, float maximum, string parameterName)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }
    }
}
