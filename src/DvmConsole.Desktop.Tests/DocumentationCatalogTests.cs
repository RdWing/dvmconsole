using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DocumentationCatalogTests
{
    [Fact]
    public void FindsPagesInDisplayOrderAndSearchesCurrentContent()
    {
        string root = CreateRoot();
        try
        {
            Write(root, "02-Second.md", "# Second\n\nAudio routing");
            Write(root, "01-First.md", "# First\n\nConsole overview");
            Write(root, Path.Combine("03-Operations", "01-PTT.md"), "# PTT\n\nGlobal transmit");
            var catalog = new DocumentationCatalog(root);

            Assert.Equal(["First", "Second", "PTT"], catalog.Find().Select(page => page.Title));
            Assert.Equal("PTT", Assert.Single(catalog.Find("global transmit")).Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsSelectedMarkdownLiveAndRejectsOutsideFiles()
    {
        string root = CreateRoot();
        string outside = Path.Combine(Path.GetTempPath(), $"dvmconsole-doc-{Guid.NewGuid():N}.md");
        try
        {
            string pagePath = Write(root, "01-Overview.md", "# First version");
            var catalog = new DocumentationCatalog(root);
            DocumentationPage page = Assert.Single(catalog.Find());

            File.WriteAllText(pagePath, "# Updated version");

            Assert.Equal("# Updated version", catalog.Read(page));
            Assert.Throws<InvalidOperationException>(() => catalog.Read(
                new DocumentationPage("Outside", "outside.md", outside)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
