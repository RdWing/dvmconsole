// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class ConfigurationExportArchiveTests
{
    [Fact]
    public async Task ArchiveContainsOnlyExportedDocumentsAndDisposalRemovesPrivateStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using (var export = new ConfigurationExportArchive(root))
            {
                await WriteAsync(export.Primary, "systems: []");
                IWritableDocument companion = await export.CreateCompanionAsync("aliases.yml");
                await WriteAsync(companion, "1001: Dispatch");
                Assert.NotNull(await export.ResolveExportedCompanionAsync("aliases.yml"));
                IReadableDocument result = await export.CreateArchiveAsync();
                await using Stream input = await result.OpenReadAsync();
                using var zip = new ZipArchive(input);
                Assert.Equal(new[] { "aliases.yml", "configuration.yaml" }, zip.Entries.Select(entry => entry.FullName).Order().ToArray());
                using var reader = new StreamReader(zip.GetEntry("aliases.yml")!.Open());
                Assert.Equal("1001: Dispatch", await reader.ReadToEndAsync());
                using var imported = await ConfigurationBundleImport.ReadAsync(result);
                IReadableDocument? alias = await imported.ResolveCompanionAsync("./aliases.yml");
                Assert.NotNull(alias);
                await using Stream aliasStream = await alias.OpenReadAsync();
                using var aliasReader = new StreamReader(aliasStream);
                Assert.Equal("1001: Dispatch", await aliasReader.ReadToEndAsync());
                Assert.Null(await imported.ResolveCompanionAsync("../aliases.yml"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => export.CreateCompanionAsync("late.yml").AsTask());
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("folder/file")]
    [InlineData("folder\\file")]
    [InlineData("C:escape")]
    [InlineData("configuration.yaml")]
    public async Task UnsafeOrConflictingCompanionNamesAreRejected(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using var export = new ConfigurationExportArchive(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => export.CreateCompanionAsync(name).AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledArchiveLeavesNoStagingAfterDisposal()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using (var export = new ConfigurationExportArchive(root))
            {
                await WriteAsync(export.Primary, "systems: []");
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.CreateArchiveAsync(new CancellationToken(true)).AsTask());
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task WriteAsync(IWritableDocument document, string text)
    {
        await using Stream stream = await document.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }
}
