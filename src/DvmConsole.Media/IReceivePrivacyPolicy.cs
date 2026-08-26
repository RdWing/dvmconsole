namespace DvmConsole.Media;

// Supplies the operator's current receive-admission policy without coupling
// protocol decoders to desktop state. Implementations may change while a call
// is active, so decoders consult the property before emitting clear audio.
public interface IReceivePrivacyPolicy
{
    bool RequireEncryptedTraffic { get; }
}
