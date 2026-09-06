// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop.Tests;

internal static class DesktopTestSessionBuilder
{
    public static MainWindowViewModelOptions CreateOptions(
        UserSettingsStore store,
        ConsoleSessionServices? services = null)
        => new(
            Document: new(store),
            Host: new(
                SerialPortProvider: static () => [],
                UiDispatcher: ImmediateTestUiDispatcher.Instance,
                SessionServices: services),
            Features: new(NetworkDisabledDemo: true));
}

internal sealed class ImmediateTestUiDispatcher : IUiDispatcher
{
    public static ImmediateTestUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;
    public void Post(Action action, bool background = false) => action();
    public ValueTask InvokeAsync(Action action)
    {
        action();
        return ValueTask.CompletedTask;
    }
}
