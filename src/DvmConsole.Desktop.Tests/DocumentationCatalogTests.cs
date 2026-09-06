// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
        Assert.Contains(pages, page => page.Title == "Configuration Troubleshooting");
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

    [Fact]
    public async Task RewritesOnlyDeclaredLocalImagesUnderDocumentationRoot()
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["03-Operations/01-PTT.md"] = "![PTT](../Assets/ptt.png)"
        };
        using var fixture = new DocumentationFixture(content, ["Assets/ptt.png"]);
        fixture.WriteBytes("Assets/ptt.png", [0x89, 0x50, 0x4E, 0x47]);

        string markdown = await fixture.Catalog.ReadAsync(Assert.Single(await fixture.Catalog.FindAsync()));

        Assert.Contains("file://", markdown, StringComparison.Ordinal);
        Assert.Contains("Assets/ptt.png", Uri.UnescapeDataString(markdown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUndeclaredAndTraversalImageReferences()
    {
        using var undeclared = new DocumentationFixture(new Dictionary<string, string>
        {
            ["01-Overview.md"] = "![private](private.png)"
        });
        DocumentationPage undeclaredPage = Assert.Single(await undeclared.Catalog.FindAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => undeclared.Catalog.ReadAsync(undeclaredPage));

        using var traversal = new DocumentationFixture(new Dictionary<string, string>
        {
            ["01-Overview.md"] = "![outside](../outside.png)"
        });
        DocumentationPage traversalPage = Assert.Single(await traversal.Catalog.FindAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => traversal.Catalog.ReadAsync(traversalPage));

        using var hostPath = new DocumentationFixture(new Dictionary<string, string>
        {
            ["01-Overview.md"] = "![private](file:///Users/example/private.png)"
        });
        DocumentationPage hostPathPage = Assert.Single(await hostPath.Catalog.FindAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => hostPath.Catalog.ReadAsync(hostPathPage));
    }

    private sealed class DocumentationFixture : IDisposable
    {
        public DocumentationFixture(
            IReadOnlyDictionary<string, string> content,
            IEnumerable<string>? assets = null)
        {
            RootPath = Directory.CreateTempSubdirectory("dvmconsole-docs-").FullName;
            foreach ((string relativePath, string markdown) in content)
                Write(relativePath, markdown);
            Catalog = new DocumentationCatalog(RootPath, content.Keys, assets);
        }

        public string RootPath { get; }
        public DocumentationCatalog Catalog { get; }

        public void Write(string relativePath, string markdown)
        {
            string path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown);
        }

        public void WriteBytes(string relativePath, byte[] content)
        {
            string path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}
