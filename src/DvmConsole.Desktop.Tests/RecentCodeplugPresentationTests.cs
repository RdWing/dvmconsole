using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecentCodeplugPresentationTests
{
    [Fact]
    public void LongPathKeepsFilenameAndElidesParentMiddle()
    {
        string path = "/Users/operator/very/long/system/configuration/archive/night-shift/dispatch.yml";

        RecentCodeplugPresentation presentation = RecentCodeplugPresentation.FromPath(path, parentBudget: 28);

        Assert.Equal("dispatch.yml", presentation.FileName);
        Assert.Contains('…', presentation.ParentPath);
        Assert.True(presentation.ParentPath.Length <= 28);
        Assert.Equal(path, presentation.FullPath);
    }

    [Theory]
    [InlineData(@"C:\dispatch\alpha\console.yml", "console.yml")]
    [InlineData("/dispatch/alpha/console.yml", "console.yml")]
    [InlineData("console.yml", "console.yml")]
    public void SeparatesPortablePathStyles(string path, string expectedFilename)
        => Assert.Equal(expectedFilename, RecentCodeplugPresentation.FromPath(path).FileName);
}
