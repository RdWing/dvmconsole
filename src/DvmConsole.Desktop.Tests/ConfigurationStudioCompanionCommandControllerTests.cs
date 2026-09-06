// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioCompanionCommandControllerTests
{
    [Fact]
    public void AddKeyCreatesCompanionAndUsesSelectedSystem()
    {
        var session = new TestSession();
        var system = new SystemConfiguration { Name = "Regional" };
        session.Configuration.Systems.Add(system);
        session.SelectedSystemForCompanion = system;
        var controller = new ConfigurationStudioCompanionCommandController(session);

        controller.AddKey();

        KeyEntry key = Assert.Single(session.Keys);
        Assert.True(session.KeyFileEnsured);
        Assert.Equal("Regional", key.System);
        Assert.Equal("p25", key.Protocol, ignoreCase: true);
        Assert.Same(key, session.CompletedKeyAddition);
        Assert.Same(session.CurrentDraft, session.CompletedBefore);
    }

    [Fact]
    public void DeleteAliasDoesNotPublishAChangeWhenCompanionRejectsRemoval()
    {
        var session = new TestSession();
        var row = new ConfigurationAliasRow("aliases.yml", new RadioAlias { Rid = 100, Alias = "Example" });
        session.SelectedAliasForCompanion = row;
        session.AllowAliasRemoval = false;
        var controller = new ConfigurationStudioCompanionCommandController(session);

        controller.DeleteAlias();

        Assert.False(session.AliasRemovalCompleted);
    }

    [Fact]
    public void AddAliasChoosesExplicitAliasSystemBeforeCurrentSystem()
    {
        var session = new TestSession();
        var current = new SystemConfiguration { Name = "Current" };
        var aliasSystem = new SystemConfiguration { Name = "Aliases" };
        session.Configuration.Systems.Add(current);
        session.Configuration.Systems.Add(aliasSystem);
        session.SelectedSystemForCompanion = current;
        session.SelectedAliasSystemForCompanion = aliasSystem;
        var controller = new ConfigurationStudioCompanionCommandController(session);

        controller.AddAlias();

        Assert.Same(aliasSystem, session.AliasSystemUsed);
        Assert.True(session.AliasAdditionCompleted);
        Assert.Same(session.CurrentDraft, session.CompletedBefore);
    }

    private sealed class TestSession : IConfigurationStudioCompanionCommandSession
    {
        public ConsoleConfiguration Configuration { get; } = new();
        public bool CanEditCompanions { get; set; } = true;
        public SystemConfiguration? SelectedSystemForCompanion { get; set; }
        public SystemConfiguration? SelectedAliasSystemForCompanion { get; set; }
        public KeyEntry? SelectedKeyForCompanion { get; set; }
        public ConfigurationAliasRow? SelectedAliasForCompanion { get; set; }
        public List<KeyEntry> Keys { get; } = [];
        public int KeyCount => Keys.Count;
        public ConfigurationStudioDraftSnapshot CurrentDraft { get; } =
            ConfigurationStudioDraftTestBuilder.CreateSnapshot("systems: []");
        public bool KeyFileEnsured { get; private set; }
        public KeyEntry? CompletedKeyAddition { get; private set; }
        public ConfigurationStudioDraftSnapshot? CompletedBefore { get; private set; }
        public SystemConfiguration? AliasSystemUsed { get; private set; }
        public bool AliasAdditionCompleted { get; private set; }
        public bool AliasRemovalCompleted { get; private set; }
        public bool AllowAliasRemoval { get; set; } = true;

        public void EnsureKeyFile() => KeyFileEnsured = true;
        public void AddKey(KeyEntry key) => Keys.Add(key);
        public void RemoveKey(KeyEntry key) => Keys.Remove(key);

        public (string Identifier, RadioAlias Alias) AddAlias(SystemConfiguration system)
        {
            AliasSystemUsed = system;
            return ("aliases.yml", new RadioAlias { Rid = 1, Alias = string.Empty });
        }

        public bool RemoveAlias(ConfigurationAliasRow row) => AllowAliasRemoval;

        public void CompleteKeyAddition(ConfigurationStudioDraftSnapshot before, KeyEntry key)
        {
            CompletedBefore = before;
            CompletedKeyAddition = key;
            SelectedKeyForCompanion = key;
        }

        public void CompleteKeyRemoval() => SelectedKeyForCompanion = null;

        public void CompleteAliasAddition(
            ConfigurationStudioDraftSnapshot before,
            string identifier,
            RadioAlias alias)
        {
            CompletedBefore = before;
            AliasAdditionCompleted = true;
            SelectedAliasForCompanion = new ConfigurationAliasRow(identifier, alias);
        }

        public void CompleteAliasRemoval(ConfigurationAliasRow row)
        {
            Assert.Same(SelectedAliasForCompanion, row);
            AliasRemovalCompleted = true;
        }
    }
}
