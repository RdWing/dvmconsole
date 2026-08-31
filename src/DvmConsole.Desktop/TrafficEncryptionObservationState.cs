using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

// Tracks encryption metadata that can arrive separately from voice. DMR clear
// is inferred when a call-start header is followed by voice without an
// intervening privacy header; a delayed explicit privacy header can correct it.
internal sealed class TrafficEncryptionObservationState(
    EncryptionSnapshot initialEncryption = default)
{
    private bool dmrPrivacyHeaderPending;

    public EncryptionSnapshot Encryption { get; private set; } = initialEncryption;

    public bool Observe(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        EncryptionSnapshot? resolved = EncryptionSnapshotResolver.TryResolve(traffic);
        if (resolved is EncryptionSnapshot explicitEncryption)
        {
            dmrPrivacyHeaderPending = false;
            return Apply(explicitEncryption);
        }

        if (traffic.Protocol != RadioMediaProtocol.Dmr)
            return false;
        if (ReceiveTrafficClassifier.IsDefinitiveStart(traffic))
        {
            dmrPrivacyHeaderPending = true;
            return false;
        }
        if (dmrPrivacyHeaderPending && ReceiveTrafficClassifier.CarriesVoicePayload(traffic))
        {
            dmrPrivacyHeaderPending = false;
            return Apply(EncryptionSnapshot.InferredClear);
        }
        if (ReceiveTrafficClassifier.IsTerminator(traffic))
            dmrPrivacyHeaderPending = false;
        return false;
    }

    private bool Apply(EncryptionSnapshot candidate)
    {
        if (!candidate.IsKnown || candidate.Evidence < Encryption.Evidence)
            return false;
        if (Encryption == candidate)
            return false;

        Encryption = candidate;
        return true;
    }
}
