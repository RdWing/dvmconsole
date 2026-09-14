// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Mobile;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class SelectedImportDocumentsTests
{
    [Theory]
    [InlineData("aliases.yml")]
    [InlineData("companions/aliases.yml")]
    [InlineData("C:\\radio\\aliases.yml")]
    [InlineData("../aliases.yml")]
    public async Task ResolvesOnlyAnExplicitlySelectedCompanion(string reference)
    {
        var companion = new Document("aliases.yml");
        var documents = new SelectedImportDocuments(new Document("console.yml"), [companion]);
        Assert.Same(companion, await documents.ResolveCompanionAsync(reference));
        Assert.Null(await documents.ResolveCompanionAsync("unselected.key"));
    }

    [Fact]
    public async Task AmbiguousNamesAreRejectedRatherThanChoosingAnArbitraryKeyFile()
    {
        var documents = new SelectedImportDocuments(new Document("console.yml"),
            [new Document("keys.yml"), new Document("KEYS.YML")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => documents.ResolveCompanionAsync("keys.yml").AsTask());
    }

    private sealed class Document(string name) : IReadableDocument
    {
        public string DisplayName => name;
        public string? OriginIdentity => null;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Resolving a document must not open it.");
    }
}
