// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class ConfigurationStudioCopyPlanTests
{
    [Fact]
    public void CopyPlanRetainsUnchangedKeysAndAliasesWithoutExpandingAnOrdinarySave()
    {
        string root = Path.Combine(Path.GetTempPath(), $"studio-copy-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "codeplug.yml");
            string keys = Path.Combine(root, "keys.clear"), aliases = Path.Combine(root, "aliases.yml");
            File.WriteAllText(keys, "keys: []\n");
            File.WriteAllText(aliases, "- rid: 42\n  alias: Dispatch\n");
            File.WriteAllText(path, """
                keyFile: ./keys.clear
                systems:
                  - name: Test
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                    aliasPath: ./aliases.yml
                zones: []
                groups: []
                """);
            ConfigurationDocument document = ConfigurationDocument.Open(path);
            var contents = new Dictionary<string, string> { [aliases] = File.ReadAllText(aliases) };
            var state = new ConfigurationStudioSaveState(document.Serialize(), keys,
                ConfigurationDocument.ComputeFileHash(keys), false, File.ReadAllText(keys), contents,
                new Dictionary<string, string> { [aliases] = ConfigurationDocument.ComputeFileHash(aliases) }, contents, []);
            Assert.Single(ConfigurationStudioFilePlanner.CreateFiles(document, document.Configuration, state, path));
            var copy = ConfigurationStudioFilePlanner.CreateFiles(document, document.Configuration, state, path,
                includeUnchangedCompanions: true);
            Assert.Equal(3, copy.Count);
            Assert.Equal(File.ReadAllText(keys), Assert.Single(copy, file => file.Category == "Encryption key file").Content);
            Assert.Equal(File.ReadAllText(aliases), Assert.Single(copy, file => file.Category == "RID alias file").Content);
            Assert.Equal(state.KeyFileHash, Assert.Single(copy, file => file.Category == "Encryption key file").ExpectedSourceHash);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
