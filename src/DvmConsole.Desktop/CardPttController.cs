using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

// Compatibility adapter for existing focused tests and callers. All PTT
// reconciliation lives in the renderer-neutral Presentation controller.
public sealed class CardPttController : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<ChannelId, ChannelViewModel> channels = [];
    private readonly ChannelPttController controller;

    public CardPttController(
        Func<ChannelViewModel, Task<bool>> start,
        Func<ChannelViewModel, Task> stop)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);
        controller = new ChannelPttController(
            async (id, _) => await start(Resolve(id)),
            async (id, _) => await stop(Resolve(id)));
    }

    public Task PressAsync(ChannelViewModel channel)
        => controller.PressAsync(Register(channel)).AsTask();

    public Task ReleaseAsync(ChannelViewModel channel)
        => controller.ReleaseAsync(Register(channel)).AsTask();

    public Task ToggleAsync(ChannelViewModel channel)
        => controller.ToggleAsync(Register(channel)).AsTask();

    public ValueTask DisposeAsync() => controller.DisposeAsync();

    private ChannelId Register(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var id = new ChannelId(channel.SessionId);
        lock (sync)
            channels[id] = channel;
        return id;
    }

    private ChannelViewModel Resolve(ChannelId id)
    {
        lock (sync)
            return channels[id];
    }
}
