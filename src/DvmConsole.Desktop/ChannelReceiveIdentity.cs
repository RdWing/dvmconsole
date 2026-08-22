namespace DvmConsole.Desktop;

// Defines the receive identity shared by zone copies of one resource.
// Keeping this comparison in one place prevents routing, presentation, and
// recording ownership from drifting apart as protocols are added.
internal static class ChannelReceiveIdentity
{
    public static bool AreEquivalent(ChannelViewModel left, ChannelViewModel right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Definition.SystemName.Equals(
                   right.Definition.SystemName,
                   StringComparison.OrdinalIgnoreCase) &&
               left.Definition.Mode.Equals(
                   right.Definition.Mode,
                   StringComparison.OrdinalIgnoreCase) &&
               left.Definition.DestinationId == right.Definition.DestinationId &&
               (!left.Definition.Mode.Equals("dmr", StringComparison.OrdinalIgnoreCase) ||
                left.Definition.Slot == right.Definition.Slot);
    }
}
