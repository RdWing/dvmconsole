// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal interface IOperatorCommandSurface
{
    MainWindowViewModel Session { get; }
    Task OpenSubscriberCommandAsync(P25SubscriberCommand command);
    void OpenTool(OperatorToolSection section);
    void ShowDebugLogs();
    void ToggleEngineeringHealth();
    void ShowDocumentation();
    void ShowAbout();
}

/// <summary>
/// Owns command identity, availability, and dispatch independently from menu
/// event adaptation. Session-bound definitions resolve the current replacement
/// session at execution time.
/// </summary>
internal sealed class OperatorCommandController
{
    private readonly OperatorCommandCatalog catalog;

    public OperatorCommandController(IOperatorCommandSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        static Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        OperatorCommandDefinition OpenSection(OperatorToolSectionDefinition definition)
            => new(definition.CommandId, () => Run(() => surface.OpenTool(definition.Section)));

        catalog = new OperatorCommandCatalog(
        [
            new(
                OperatorCommandIds.Connect,
                () => Run(() => surface.Session.ConnectCommand.Execute(null)),
                () => surface.Session.ConnectCommand.CanExecute(null)),
            new(
                OperatorCommandIds.Disconnect,
                () => Run(() => surface.Session.DisconnectCommand.Execute(null)),
                () => surface.Session.DisconnectCommand.CanExecute(null)),
            OperatorCommandDefinition.BindCurrent(
                OperatorCommandIds.EnableAllReceive,
                () => surface.Session,
                current => current.EnableAllReceiveAsync()),
            OperatorCommandDefinition.BindCurrent(
                OperatorCommandIds.DisableAllReceive,
                () => surface.Session,
                current => current.DisableAllReceiveAsync()),
            OperatorCommandDefinition.BindCurrent(
                OperatorCommandIds.EnableZoneReceive,
                () => surface.Session,
                current => current.EnableSelectedZoneReceiveAsync(),
                current => current.HasSelectedZone),
            OperatorCommandDefinition.BindCurrent(
                OperatorCommandIds.DisableZoneReceive,
                () => surface.Session,
                current => current.DisableSelectedZoneReceiveAsync(),
                current => current.HasSelectedZone),
            new(
                OperatorCommandIds.ToggleAllTransmit,
                () => Run(surface.Session.ToggleAllTransmitSelection)),
            new(
                OperatorCommandIds.SubscriberPage,
                () => surface.OpenSubscriberCommandAsync(P25SubscriberCommand.CallAlert)),
            new(
                OperatorCommandIds.SubscriberRadioCheck,
                () => surface.OpenSubscriberCommandAsync(P25SubscriberCommand.RadioCheck)),
            new(
                OperatorCommandIds.SubscriberInhibit,
                () => surface.OpenSubscriberCommandAsync(P25SubscriberCommand.Inhibit)),
            new(
                OperatorCommandIds.SubscriberUninhibit,
                () => surface.OpenSubscriberCommandAsync(P25SubscriberCommand.Uninhibit)),
            .. OperatorToolSectionCatalog.All.Select(OpenSection),
            new(OperatorCommandIds.DebugLogs, () => Run(surface.ShowDebugLogs)),
            new(OperatorCommandIds.ToggleEngineeringHealth, () => Run(surface.ToggleEngineeringHealth)),
            new(OperatorCommandIds.Documentation, () => Run(surface.ShowDocumentation)),
            new(OperatorCommandIds.About, () => Run(surface.ShowAbout))
        ]);
    }

    public Task ExecuteAsync(string commandId) => catalog.ExecuteAsync(commandId);
}
