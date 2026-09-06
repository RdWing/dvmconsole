// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ModelessWindowControllerTests
{
    [Fact]
    public void SessionReplacementClosesOnlySessionBoundWindows()
    {
        var controller = new ModelessWindowController();
        var session = new FakeWindow();
        var application = new FakeWindow();
        controller.Track("session", session, sessionBound: true, window => window.Close());
        controller.Track("application", application, sessionBound: false, window => window.Close());

        controller.CloseSessionBound();

        Assert.True(session.Closed);
        Assert.False(application.Closed);
        Assert.Null(controller.Get<FakeWindow>("session"));
        Assert.Same(application, controller.Get<FakeWindow>("application"));
    }

    [Fact]
    public void StaleClosedCallbackCannotForgetAReplacementWindow()
    {
        var controller = new ModelessWindowController();
        var original = new FakeWindow();
        controller.Track("tool", original, true, window => window.Close());
        controller.Forget("tool", original);
        var replacement = new FakeWindow();
        controller.Track("tool", replacement, true, window => window.Close());

        controller.Forget("tool", original);

        Assert.Same(replacement, controller.Get<FakeWindow>("tool"));
    }

    [Fact]
    public void CloseAllAttemptsEveryWindowBeforeReportingFailures()
    {
        var controller = new ModelessWindowController();
        var first = new FakeWindow { Failure = new IOException("first") };
        var second = new FakeWindow();
        controller.Track("first", first, true, window => window.Close());
        controller.Track("second", second, false, window => window.Close());

        AggregateException failure = Assert.Throws<AggregateException>(controller.CloseAll);

        Assert.Single(failure.InnerExceptions);
        Assert.True(second.Closed);
        Assert.Null(controller.Get<FakeWindow>("first"));
        Assert.Null(controller.Get<FakeWindow>("second"));
    }

    private sealed class FakeWindow
    {
        public bool Closed { get; private set; }
        public Exception? Failure { get; init; }
        public void Close()
        {
            Closed = true;
            if (Failure is not null)
                throw Failure;
        }
    }
}
