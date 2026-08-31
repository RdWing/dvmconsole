namespace DvmConsole.Vocoder;

public sealed class NativeVocoderFactory : IVocoderFactory
{
    public IVocoderBackend Create(
        IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null)
        => new SoftwareVocoderBackend(receiveAudioProcessingOptions);
}
