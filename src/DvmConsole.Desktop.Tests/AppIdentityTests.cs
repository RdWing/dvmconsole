using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public void AvaloniaApplicationNameMatchesProductName()
    {
        var app = new App();
        app.Initialize();

        Assert.Equal("DVM Console NEO", app.Name);
    }
}
