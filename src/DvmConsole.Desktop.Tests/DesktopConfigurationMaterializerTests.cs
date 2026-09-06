// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopConfigurationMaterializerTests
{
    [Fact]
    public async Task LeaseKeepsMaterializationAliveAndDeletesItOnDispose()
    {
        string root = CreateRoot();
        try
        {
            ConfigurationReference reference = await ImportConfigurationAsync(root);
            string runtimeRoot = Path.Combine(root, "runtime");
            var materializer = new DesktopConfigurationMaterializer(
                new ManagedConfigurationLibrary(Path.Combine(root, "library")),
                runtimeRoot);

            IConfigurationMaterializationLease lease = await materializer.MaterializeAsync(reference);
            string directory = Path.GetDirectoryName(lease.Path)!;
            Assert.True(File.Exists(lease.Path));

            _ = new DesktopConfigurationMaterializer(
                new ManagedConfigurationLibrary(Path.Combine(root, "library")),
                runtimeRoot);
            Assert.True(Directory.Exists(directory));

            await lease.DisposeAsync();

            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupRemovesOnlyUnlockedRuntimeDirectories()
    {
        string root = CreateRoot();
        try
        {
            string runtimeRoot = Path.Combine(root, "runtime");
            string legacyOrphan = Path.Combine(runtimeRoot, "legacy-configuration", "revision");
            Directory.CreateDirectory(legacyOrphan);
            File.WriteAllText(Path.Combine(legacyOrphan, "codeplug.yml"), "systems: []\n");

            _ = new DesktopConfigurationMaterializer(
                new ManagedConfigurationLibrary(Path.Combine(root, "library")),
                runtimeRoot);

            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "legacy-configuration")));
            Assert.True(File.Exists(Path.Combine(runtimeRoot, ".runtime.lock")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ConfigurationReference> ImportConfigurationAsync(string root)
    {
        string sourcePath = Path.Combine(root, "source", "codeplug.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, """
            systems:
              - name: Test
                identity: Console
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
            zones: []
            groups: []
            """);
        var library = new ManagedConfigurationLibrary(Path.Combine(root, "library"));
        ConfigurationImportResult result = await library.ImportAsync(
            new DesktopConfigurationDocumentSet(sourcePath),
            new ConfigurationImportOptions());
        return result.Reference;
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-materializer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
