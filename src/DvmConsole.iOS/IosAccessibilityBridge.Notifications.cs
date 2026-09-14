// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

internal sealed partial class IosAccessibilityBridge
{
    private readonly NSObject voiceOverObserver;
    private PeerElement[] visibleElements = [];
    private long structureRevision;
    private long announcedRevision;
    private bool refreshPending;
    private bool disposed;

    private void HandleLayoutUpdated(object? sender, EventArgs args) => ScheduleLayoutRefresh();

    private void ScheduleLayoutRefresh()
    {
        if (disposed || refreshPending || !UIAccessibility.IsVoiceOverRunning) return;
        refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            refreshPending = false;
            if (disposed || !UIAccessibility.IsVoiceOverRunning) return;
            using var current = ReadElements();
            if (announcedRevision == structureRevision) return;
            announcedRevision = structureRevision;
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.LayoutChanged, null);
        }, DispatcherPriority.Background);
    }
}
