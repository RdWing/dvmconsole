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

        return left.SessionDefinition.RouteKey == right.SessionDefinition.RouteKey;
    }
}
