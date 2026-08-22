namespace DvmConsole.Desktop;

// A resource can appear on more than one zone card while only one card owns
// its decoder. Route decoded PCM to the armed TAR card without decoding the
// same network frame more than once.
internal static class ReceiveRecordingTargetResolver
{
    public static ChannelViewModel? Resolve(
        ChannelViewModel decodedChannel,
        IReadOnlyList<ChannelViewModel> resourceCandidates)
    {
        ArgumentNullException.ThrowIfNull(decodedChannel);
        ArgumentNullException.ThrowIfNull(resourceCandidates);

        if (decodedChannel.IsRecordingEnabled)
            return decodedChannel;

        for (int index = 0; index < resourceCandidates.Count; index++)
        {
            ChannelViewModel candidate = resourceCandidates[index];
            if (candidate.IsRecordingEnabled &&
                ChannelReceiveIdentity.AreEquivalent(decodedChannel, candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
