
namespace DvmConsole.Desktop;

internal readonly record struct ChannelTrafficApplyResult(
    bool Matched,
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null)
{
    public static ChannelTrafficApplyResult NoMatch => new(false, ReceiveStreamTransition.None);
}
