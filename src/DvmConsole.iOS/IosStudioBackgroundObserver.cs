// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Threading;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Bounds the active editor's recovery write when UIKit backgrounds the app.</summary>
internal sealed class IosStudioBackgroundObserver : IDisposable
{
    private readonly NSObject observer;
    private readonly IosBackgroundCheckpoint allowance = new();

    public IosStudioBackgroundObserver(Func<CancellationToken, Task> checkpoint)
    {
        observer = UIApplication.Notifications.ObserveDidEnterBackground((_, _) =>
            TaskObservation.Observe(allowance.RunAsync(checkpoint)));
    }

    public void Dispose() => observer.Dispose();
}
