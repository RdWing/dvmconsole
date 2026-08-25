using DvmConsole.Operations;

namespace DvmConsole.Desktop;

internal readonly record struct ChannelTrafficApplyResult(
    bool Matched,
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null,
    DateTimeOffset? EndedAt = null)
{
    public static ChannelTrafficApplyResult NoMatch => new(false, ReceiveStreamTransition.None);
}
