// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    public ConsoleWebStreamSession? WebSession { get; private set; }

    public void InitializeWebSession(IReadOnlyList<WebStreamPlaybackDescriptor> definitions,
        IConsoleWebStreamPreferences? preferences, ConsoleWebPlaybackDependencies dependencies)
    {
        if (WebSession is not null)
            throw new InvalidOperationException("Web stream session is already initialized.");
        WebSession = new ConsoleWebStreamSession(definitions, preferences,
            observer => InitializeWebPlayback(dependencies, observer));
    }
}
