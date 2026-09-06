// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioCompanionStateTests
{
    [Fact]
    public void LoadedCompanionsRemainCleanUntilTheirParsedContentChanges()
    {
        var configuration = new ConsoleConfiguration
        {
            KeyFile = "keys.clear",
            Systems =
            [
                new SystemConfiguration { Name = "North", AliasPath = "aliases.yml" }
            ]
        };
        var state = new ConfigurationStudioCompanionState();
        state.Load(
            new ConfigurationStudioCompanionSnapshot(
                new ConfigurationStudioKeyCompanion(
                    "keys.clear",
                    "keys:\n  - name: Dispatch\n    system: North\n    protocol: p25\n    algId: 132\n    keyId: 0x19\n    key: 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF\n",
                    "key-hash",
                    null,
                    false),
                [
                    new ConfigurationStudioAliasCompanion(
                        "aliases.yml",
                        ["aliases.yml"],
                        "- rid: 1001\n  alias: Unit 1\n",
                        "alias-hash")
                ],
                [],
                []),
            configuration);

        Assert.True(state.ReferencesMatch(configuration));
        Assert.False(state.IsKeyFileDirty);
        Assert.False(state.AliasFilesDirty);

        state.Keys.Keys[0].Name = "Changed";
        state.MarkKeyContentChanged();
        RadioAlias alias = Assert.Single(state.AliasTables["aliases.yml"]);
        alias.Alias = "Changed Unit";
        state.MarkAliasContentChanged("aliases.yml");

        Assert.True(state.IsKeyFileDirty);
        Assert.True(state.AliasFilesDirty);
    }

    [Fact]
    public void SnapshotRestoreRecoversCompanionContentsAndDirtyBaselines()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "North" }]
        };
        var state = new ConfigurationStudioCompanionState();
        state.EnsureKeyFile(configuration);
        state.AddKey(new KeyEntry
        {
            Name = "Dispatch",
            System = "North",
            Protocol = "p25",
            AlgId = 132,
            KeyId = 25,
            Key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"
        });
        (string aliasIdentifier, RadioAlias alias) = state.AddAlias(
            configuration,
            configuration.Systems[0]);
        alias.Rid = 1001;
        alias.Alias = "Unit 1";
        var snapshot = state.CaptureSnapshot();

        state.Keys.Keys[0].Name = "Mutated";
        alias.Alias = "Mutated";
        state.Restore(snapshot);

        Assert.Equal("Dispatch", Assert.Single(state.Keys.Keys).Name);
        Assert.Equal("Unit 1", Assert.Single(state.AliasTables[aliasIdentifier]).Alias);
        Assert.True(state.IsKeyFileDirty);
        Assert.True(state.AliasFilesDirty);
    }

    [Fact]
    public void SystemRenameUpdatesScopedKeysWithoutChangingAliasContent()
    {
        var state = new ConfigurationStudioCompanionState();
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "Old" }]
        };
        state.EnsureKeyFile(configuration);
        state.AddKey(new KeyEntry { Name = "Scoped", System = "Old", Protocol = "dmr" });

        state.ApplySystemRename("Old", "New");

        Assert.Equal("New", Assert.Single(state.Keys.Keys).System);
    }

    [Fact]
    public void UnchangedCompanionSnapshotsShareTheirImmutableProjection()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems = [new SystemConfiguration { Name = "North" }]
        };
        var state = new ConfigurationStudioCompanionState();
        state.EnsureKeyFile(configuration);

        ConfigurationStudioReferencedFilesSnapshot first = state.CaptureSnapshot();
        ConfigurationStudioReferencedFilesSnapshot second = state.CaptureSnapshot();
        state.AddKey(new KeyEntry { Name = "Dispatch", System = "North", Protocol = "dmr" });
        ConfigurationStudioReferencedFilesSnapshot changed = state.CaptureSnapshot();

        Assert.Same(first, second);
        Assert.NotSame(first, changed);
    }

    [Fact]
    public void InvalidKeyAttachmentDoesNotMutateTheDraftReferenceOrContent()
    {
        var configuration = new ConsoleConfiguration
        {
            KeyFile = "existing.clear",
            Systems = [new SystemConfiguration { Name = "North" }]
        };
        var state = new ConfigurationStudioCompanionState();
        state.Load(
            new ConfigurationStudioCompanionSnapshot(
                new ConfigurationStudioKeyCompanion(
                    "existing.clear",
                    "keys: []\n",
                    "hash",
                    null,
                    false),
                [],
                [],
                []),
            configuration);

        Assert.ThrowsAny<Exception>(() =>
            state.AttachKeyFile(configuration, "replacement.clear", "keys: ["));

        Assert.Equal("existing.clear", configuration.KeyFile);
        Assert.Equal("existing.clear", state.KeyFileIdentifier);
        Assert.Empty(state.Keys.Keys);
    }

    [Fact]
    public void ReplacingOneAliasReferencePreservesASharedResolvedCompanion()
    {
        var first = new SystemConfiguration { Name = "North", AliasPath = "aliases.yml" };
        var second = new SystemConfiguration { Name = "South", AliasPath = "./aliases.yml" };
        var configuration = new ConsoleConfiguration { Systems = [first, second] };
        var state = new ConfigurationStudioCompanionState();
        state.Load(
            new ConfigurationStudioCompanionSnapshot(
                null,
                [
                    new ConfigurationStudioAliasCompanion(
                        "managed-aliases",
                        ["aliases.yml", "./aliases.yml"],
                        "- rid: 1001\n  alias: Unit 1\n",
                        "alias-hash")
                ],
                [],
                []),
            configuration);
        ConfigurationStudioReferencedFilesSnapshot before = state.CaptureSnapshot();

        state.AttachAliasFile(
            configuration,
            first,
            "replacement.yml",
            "- rid: 2001\n  alias: Unit 2\n");

        Assert.Equal("managed-aliases", state.FindAliasTableIdentifier(second.AliasPath));
        Assert.True(state.AliasTables.ContainsKey("managed-aliases"));

        state.Restore(before);
        Assert.Equal("managed-aliases", state.FindAliasTableIdentifier("aliases.yml"));
        Assert.Equal("managed-aliases", state.FindAliasTableIdentifier("./aliases.yml"));
    }
}
