namespace DvmConsole.Vocoder;

public interface IVocoderFactory
{
    IVocoderBackend Create(
        IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null);
}
