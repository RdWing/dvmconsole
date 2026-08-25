using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DocumentationCatalogTests
{
    [Fact]
    public async Task DefaultCatalogReadsPackagedGuide()
    {
        DocumentationCatalog catalog = DocumentationCatalog.OpenDefault();

        IReadOnlyList<DocumentationPage> pages = await catalog.FindAsync();

        Assert.NotEmpty(pages);
        Assert.All(pages, page => Assert.True(File.Exists(page.FilePath)));
        Assert.Contains("DVM Console", await catalog.ReadAsync(pages[0]));
    }

    [Fact]
    public async Task FindsPagesInDisplayOrderAndSearchesCurrentContent()
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["02-Second.md"] = "# Second\n\nAudio routing",
            ["01-First.md"] = "# First\n\nConsole overview",
            ["03-Operations/01-PTT.md"] = "# PTT\n\nGlobal transmit"
        };
        using var fixture = new DocumentationFixture(content);

        Assert.Equal(["First", "Second", "PTT"], (await fixture.Catalog.FindAsync()).Select(page => page.Title));
        Assert.Equal("PTT", Assert.Single(await fixture.Catalog.FindAsync("global transmit")).Title);
    }

    [Fact]
    public async Task ReadsSelectedMarkdownLiveAndRejectsUnknownPages()
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01-Overview.md"] = "# First version"
        };
        using var fixture = new DocumentationFixture(content);
        DocumentationPage page = Assert.Single(await fixture.Catalog.FindAsync());

        fixture.Write("01-Overview.md", "# Updated version");

        Assert.Equal("# Updated version", await fixture.Catalog.ReadAsync(page));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.ReadAsync(
            new DocumentationPage("Outside", "outside.md", Path.Combine(fixture.RootPath, "outside.md"))));
    }

    private sealed class DocumentationFixture : IDisposable
    {
        public DocumentationFixture(IReadOnlyDictionary<string, string> content)
        {
            RootPath = Directory.CreateTempSubdirectory("dvmconsole-docs-").FullName;
            foreach ((string relativePath, string markdown) in content)
                Write(relativePath, markdown);
            Catalog = new DocumentationCatalog(RootPath, content.Keys);
        }

        public string RootPath { get; }
        public DocumentationCatalog Catalog { get; }

        public void Write(string relativePath, string markdown)
        {
            string path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown);
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}
