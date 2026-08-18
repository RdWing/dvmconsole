using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorDialogFactoryTests
{
    [Fact]
    public void MessageDialogsAreContentSizedAndInternallyBounded()
    {
        OperatorDialogLayout layout = OperatorDialogFactory.Layout;

        Assert.Equal(Avalonia.Controls.SizeToContent.Height, layout.SizeToContent);
        Assert.InRange(layout.MaxHeight, 400, 700);
        Assert.InRange(layout.MessageMaxHeight, 200, 500);
        Assert.False(layout.CanResize);
    }
}
