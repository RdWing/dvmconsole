// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession
{
    private P25KeyRetrievalCoordinator? p25KeyRetrieval;

    private void InitializeP25KeyRetrieval()
    {
        if (dependencies.P25 is not P25KeyRing keys) return;
        p25KeyRetrieval = services.Connection.OwnAsync("p25-key-retrieval",
            new P25KeyRetrievalCoordinator(keys, dependencies.Host.Delay.DelayAsync));
    }

    private void OnP25KeyReceived(object? sender, RadioP25KeyResponse response)
        => radioLifecycle.OnKey(sender, response);
}
