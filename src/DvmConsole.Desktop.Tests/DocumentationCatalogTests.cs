using System.Net;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DocumentationCatalogTests
{
    [Fact]
    public async Task FindsRemotePagesInDisplayOrderAndSearchesCurrentContent()
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["02-Second.md"] = "# Second\n\nAudio routing",
            ["01-First.md"] = "# First\n\nConsole overview",
            ["03-Operations/01-PTT.md"] = "# PTT\n\nGlobal transmit"
        };
        DocumentationCatalog catalog = CreateCatalog(content);

        Assert.Equal(["First", "Second", "PTT"], (await catalog.FindAsync()).Select(page => page.Title));
        Assert.Equal("PTT", Assert.Single(await catalog.FindAsync("global transmit")).Title);
    }

    [Fact]
    public async Task ReadsSelectedMarkdownLiveAndRejectsUnknownUrls()
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01-Overview.md"] = "# First version"
        };
        DocumentationCatalog catalog = CreateCatalog(content);
        DocumentationPage page = Assert.Single(await catalog.FindAsync());

        content["01-Overview.md"] = "# Updated version";

        Assert.Equal("# Updated version", await catalog.ReadAsync(page));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ReadAsync(
            new DocumentationPage("Outside", "outside.md", new Uri("https://example.test/outside.md"))));
    }

    private static DocumentationCatalog CreateCatalog(Dictionary<string, string> content)
    {
        var client = new HttpClient(new DocumentationHandler(content));
        return new DocumentationCatalog(client, new Uri("https://example.test/docs/"), content.Keys);
    }

    private sealed class DocumentationHandler(Dictionary<string, string> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath["/docs/".Length..]);
            var response = content.TryGetValue(path, out string? markdown)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(markdown) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
