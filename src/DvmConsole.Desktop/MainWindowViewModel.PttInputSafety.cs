// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal void SetSpacePttInputSuppressed(bool suppressed)
        => pttSession.SetSpaceInputSuppressed(suppressed);

    internal ValueTask StopKeyboardPttAsync(CancellationToken cancellationToken = default)
        => pttSession.StopAsync(cancellationToken);
}
