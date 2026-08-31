namespace DvmConsole.Application;

public interface IConsoleCommands
{
    ValueTask SetReceiveEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken = default);

    ValueTask BeginPttAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default);

    ValueTask EndPttAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default);

    ValueTask SetTransmitSelectedAsync(
        ChannelId channelId,
        bool selected,
        CancellationToken cancellationToken = default);

    ValueTask SetPageSelectedAsync(
        ChannelId channelId,
        bool selected,
        CancellationToken cancellationToken = default);

    ValueTask SetAlertSelectedAsync(
        ChannelId channelId,
        bool selected,
        CancellationToken cancellationToken = default);

    ValueTask SetTransmitEncryptedAsync(
        ChannelId channelId,
        bool encrypted,
        CancellationToken cancellationToken = default);

    ValueTask SetChannelGainAsync(
        ChannelId channelId,
        double gain,
        CancellationToken cancellationToken = default);

    ValueTask SetChannelBalanceAsync(
        ChannelId channelId,
        double balance,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpConsoleCommands : IConsoleCommands
{
    public ValueTask SetReceiveEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask BeginPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask EndPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetTransmitSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetPageSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetAlertSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetTransmitEncryptedAsync(ChannelId channelId, bool encrypted, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetChannelGainAsync(ChannelId channelId, double gain, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask SetChannelBalanceAsync(ChannelId channelId, double balance, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
