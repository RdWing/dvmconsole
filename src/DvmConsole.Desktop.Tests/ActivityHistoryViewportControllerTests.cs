// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using DvmConsole.Desktop;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

[Collection(AvaloniaControlTestCollection.Name)]
public sealed class ActivityHistoryViewportControllerTests
{
    [Fact]
    public void TopInsertionCapturesAndRestoresUntilAnchorSettles()
    {
        var anchor = new FakeViewportAnchor { HasPendingRestore = true };
        using var controller = new ActivityHistoryViewportController(new Border(), anchor);

        controller.HandleCollectionChanging(
            null,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                new object(),
                0));
        controller.RestoreAfterLayout();

        Assert.Equal(1, anchor.CaptureCount);
        Assert.Equal(1, anchor.RestoreCount);

        anchor.HasPendingRestore = false;
        controller.RestoreAfterLayout();
        Assert.Equal(2, anchor.RestoreCount);
    }

    [Fact]
    public void ResetAndDisposalClearPendingAnchorIdempotently()
    {
        var anchor = new FakeViewportAnchor { HasPendingRestore = true };
        var controller = new ActivityHistoryViewportController(new Border(), anchor);

        controller.HandleCollectionChanging(
            null,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        controller.Dispose();

        Assert.Equal(2, anchor.ResetCount);
    }

    private sealed class FakeViewportAnchor : IScrollViewportAnchor
    {
        public bool HasPendingRestore { get; set; }
        public int CaptureCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int ResetCount { get; private set; }

        public void Capture() => CaptureCount++;

        public void Restore() => RestoreCount++;

        public void Reset()
        {
            ResetCount++;
            HasPendingRestore = false;
        }
    }
}
