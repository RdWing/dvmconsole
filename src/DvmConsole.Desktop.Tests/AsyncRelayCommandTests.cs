using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public void CanExecuteNotificationIsMarshaledWhenCallerLacksUiAccess()
    {
        int posted = 0;
        int raised = 0;
        var command = new AsyncRelayCommand(
            () => Task.CompletedTask,
            () => true,
            checkUiAccess: () => false,
            postToUi: action =>
            {
                posted++;
                action();
            });
        command.CanExecuteChanged += (_, _) => raised++;

        command.RaiseCanExecuteChanged();

        Assert.Equal(1, posted);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CanExecuteNotificationRunsDirectlyWithUiAccess()
    {
        int posted = 0;
        int raised = 0;
        var command = new AsyncRelayCommand(
            () => Task.CompletedTask,
            () => true,
            checkUiAccess: () => true,
            postToUi: _ => posted++);
        command.CanExecuteChanged += (_, _) => raised++;

        command.RaiseCanExecuteChanged();

        Assert.Equal(0, posted);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Execute_ReportsFaultAndRestoresCanExecute()
    {
        var expected = new InvalidOperationException("command failure");
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => Task.FromException(expected),
            () => true,
            checkUiAccess: () => true,
            postToUi: action => action(),
            reportFault: exception => observed.TrySetResult(exception));

        command.Execute(null);

        Assert.Same(expected, await observed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Execute_TreatsCancellationAsExpectedCompletion()
    {
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => Task.FromCanceled(new CancellationToken(canceled: true)),
            () => true,
            checkUiAccess: () => true,
            postToUi: action => action(),
            reportFault: exception => reported.TrySetResult(exception));

        command.Execute(null);
        await Task.Delay(25);

        Assert.False(reported.Task.IsCompleted);
        Assert.True(command.CanExecute(null));
    }
}
