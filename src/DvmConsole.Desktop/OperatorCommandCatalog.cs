namespace DvmConsole.Desktop;

internal static class OperatorCommandIds
{
    public const string Connect = "session.connect";
    public const string Disconnect = "session.disconnect";
    public const string EnableAllReceive = "channels.receive.enable-all";
    public const string DisableAllReceive = "channels.receive.disable-all";
    public const string EnableZoneReceive = "channels.receive.enable-zone";
    public const string DisableZoneReceive = "channels.receive.disable-zone";
    public const string ToggleAllTransmit = "channels.transmit.toggle-all";
    public const string SubscriberPage = "subscriber.page";
    public const string SubscriberRadioCheck = "subscriber.radio-check";
    public const string SubscriberInhibit = "subscriber.inhibit";
    public const string SubscriberUninhibit = "subscriber.uninhibit";
    public const string DebugLogs = "view.debug-logs";
    public const string ToggleEngineeringHealth = "view.engineering-health";
    public const string Documentation = "help.documentation";
    public const string About = "help.about";
}

internal sealed class OperatorCommandDefinition
{
    private readonly Func<bool> canExecute;
    private readonly Func<Task> executeAsync;

    public OperatorCommandDefinition(
        string id,
        Func<Task> executeAsync,
        Func<bool>? canExecute = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute ?? (() => true);
    }

    public string Id { get; }

    public Task ExecuteAsync()
        => canExecute() ? executeAsync() : Task.CompletedTask;
}

internal sealed class OperatorCommandCatalog
{
    private readonly IReadOnlyDictionary<string, OperatorCommandDefinition> commandsById;

    public OperatorCommandCatalog(IEnumerable<OperatorCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        OperatorCommandDefinition[] snapshot = commands.ToArray();
        if (snapshot.Length == 0)
            throw new ArgumentException("At least one operator command is required.", nameof(commands));
        commandsById = snapshot.ToDictionary(command => command.Id, StringComparer.OrdinalIgnoreCase);
    }

    public Task ExecuteAsync(string id)
        => commandsById.TryGetValue(id, out OperatorCommandDefinition? command)
            ? command.ExecuteAsync()
            : Task.CompletedTask;
}
