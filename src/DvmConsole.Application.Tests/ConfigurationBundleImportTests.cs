// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConfigurationBundleImportTests
{
    [Theory]
    [InlineData("../keys.clear")]
    [InlineData("folder/keys.clear")]
    [InlineData("CONFIGURATION.yaml")]
    public async Task RejectsUnsafeOrDuplicateNames(string name)
    {
        var document = Bundle("configuration.yaml", name);
        await Assert.ThrowsAsync<InvalidDataException>(() => ConfigurationBundleImport.ReadAsync(document));
    }

    [Fact]
    public async Task RequiresPrimaryYaml()
        => await Assert.ThrowsAsync<InvalidDataException>(() => ConfigurationBundleImport.ReadAsync(Bundle("aliases.yml")));

    [Fact]
    public async Task HonorsCancellation()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConfigurationBundleImport.ReadAsync(Bundle("configuration.yaml"), new CancellationToken(true)));

    [Fact]
    public async Task RejectsOversizedExpandedData()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using Stream entry = zip.CreateEntry("configuration.yaml").Open();
            byte[] block = new byte[1024 * 1024];
            for (int i = 0; i < 65; i++) entry.Write(block);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ConfigurationBundleImport.ReadAsync(new Document(bytes.ToArray())));
    }

    private static Document Bundle(params string[] names)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (string name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("systems: []");
            }
        return new Document(bytes.ToArray());
    }

    private sealed record Document(byte[] Bytes) : IReadableDocument
    {
        public string DisplayName => "bundle.zip";
        public string? OriginIdentity => "test:bundle";
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(Bytes));
        }
    }
}
