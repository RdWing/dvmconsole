#nullable enable

using System.Runtime.CompilerServices;

namespace fnecore;

// Adapts the pinned fnecore peer without maintaining a private source fork.
// FnePeer clears its fallback stream after configuration, then uses that
// fallback for keepalive packets. Current masters reject the resulting zero
// stream until inbound traffic happens to replace it.
internal static class FnePeerKeepaliveStreamInitializer
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "streamId")]
    private static extern ref uint GetStreamId(FnePeer peer);

    public static bool TryInitialize(FnePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        ref uint streamId = ref GetStreamId(peer);
        if (streamId != 0)
            return false;

        do
        {
            streamId = FneBase.CreateStreamID();
        }
        while (streamId == 0);

        return true;
    }
}
