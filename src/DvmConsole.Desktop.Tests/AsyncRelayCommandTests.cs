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
}
