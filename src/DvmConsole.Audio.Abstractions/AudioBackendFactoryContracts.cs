namespace DvmConsole.Audio;

public sealed record AudioBackendConfiguration(
    AudioProcessingMode ProcessingMode,
    string InputDeviceId,
    string OutputDeviceId)
{
    public static AudioBackendConfiguration Default { get; } = new(
        AudioProcessingMode.DvmConsole,
        "default",
        "default");
}

public interface IAudioBackendFactory
{
    IAudioBackend Create(AudioBackendConfiguration configuration);
}
