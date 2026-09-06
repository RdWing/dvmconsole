// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public void LoadsImmutableLegacyAliasFixture()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Compatibility",
            "legacy-codeplug-aliases.yml");

        ConsoleConfiguration configuration = ConfigurationLoader.Load(path);

        Assert.Equal(Path.GetFullPath(path), configuration.SourcePath);
        Assert.Equal("../keys.example.clear", configuration.KeyFile);
        SystemConfiguration system = Assert.Single(configuration.Systems);
        Assert.Equal("Radio 1", AliasFileLoader.FindAlias(system.RidAlias, 1));
        Assert.Equal("Radio 1", system.AliasIndex.Find(1));
        GroupConfiguration group = Assert.Single(configuration.Groups);
        Assert.Equal("Legacy Patch", group.Name);
        Assert.Empty(configuration.LegacyPatchGroups);
        ZoneConfiguration zone = Assert.Single(configuration.Zones);
        WebStreamConfiguration stream = Assert.Single(zone.WebStreams);
        Assert.Equal("Legacy Stream", stream.Name);
        ChannelConfiguration channel = Assert.Single(zone.Channels);
        Assert.True(channel.RxOnly);
        Assert.True(channel.SelectableEncryption);
        Assert.Equal("small", channel.CardSize);
        Assert.Empty(ConfigurationLoader.Validate(configuration));
    }

    [Fact]
    public void RadioAliasIndexIsImmutableAndPreservesFirstMatchSemantics()
    {
        var aliases = new List<RadioAlias>
        {
            new() { Rid = 42, Alias = "First" },
            new() { Rid = 42, Alias = "Second" }
        };
        var index = new RadioAliasIndex(aliases);

        aliases[0].Alias = "Changed";
        aliases.Add(new RadioAlias { Rid = 43, Alias = "Late" });

        Assert.Equal("First", index.Find(42));
        Assert.Equal(string.Empty, index.Find(43));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void LoadsTheLegacyExampleCodeplug()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "codeplug.example.yml");

        ConsoleConfiguration configuration = ConfigurationLoader.Load(path);

        Assert.Single(configuration.Systems);
        Assert.Equal("System 1", configuration.Systems[0].Name);
        Assert.Equal(3, configuration.Zones.Count);
        Assert.Equal("Channel 1", configuration.Zones[0].Channels[0].Name);
        Assert.Empty(ConfigurationLoader.Validate(configuration));
    }

    [Fact]
    public void ValidatesTransportEncryptionCompatibilityMode()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration
                {
                    Name = "System 1",
                    Address = "127.0.0.1",
                    Port = 62031,
                    TransportEncryptionMode = "gcm"
                }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Contains(errors, error => error.Contains(
            "unsupported transport encryption mode 'gcm'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationUsesTheSameTrimmedCaseInsensitiveIdentitiesAsRuntime()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration { Name = " North ", Address = "127.0.0.1", Port = 62031 }
            ],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Dispatch",
                    Channels =
                    [
                        new ChannelConfiguration
                        {
                            Name = "Primary",
                            System = "North",
                            Tgid = "101",
                            Mode = " P25 ",
                            CardSize = " Large "
                        }
                    ]
                }
            ]
        };

        Assert.Empty(ConfigurationLoader.Validate(configuration));

        configuration.Systems.Add(new SystemConfiguration
        {
            Name = "north",
            Address = "127.0.0.1",
            Port = 62031
        });
        Assert.Contains(
            ConfigurationLoader.Validate(configuration),
            error => error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidationRejectsUnsupportedGroupTypesAndReservedMetadataPort()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration { Name = "North", Address = "127.0.0.1", Port = 65535 }
            ],
            Groups =
            [
                new GroupConfiguration { Name = "Mystery", Type = "not-a-real-type" }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("invalid port", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unsupported type", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsEncryptedTransportWithoutChangingPlaintextConfigurationLoading()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration
                {
                    Name = "Missing PSK",
                    Address = "127.0.0.1",
                    Port = 62031,
                    Password = "password",
                    Encrypted = true
                },
                new SystemConfiguration
                {
                    Name = "Plaintext",
                    Address = "127.0.0.1",
                    Port = 62031,
                    Encrypted = false
                }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Contains(errors, error => error.Contains(
            "'Missing PSK' must have a preshared key",
            StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains(
            "'Plaintext'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvesRelativePathsFromTheCodeplugDirectory()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "codeplug.example.yml");
        ConsoleConfiguration configuration = ConfigurationLoader.Load(path);

        string resolved = ConfigurationLoader.ResolvePath(configuration, "keys.clear");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(path)!, "keys.clear"),
            resolved);
    }

    [Fact]
    public void LoadsLegacyKeyAndAliasFiles()
    {
        string testData = Path.Combine(AppContext.BaseDirectory, "TestData");

        KeyContainer keys = KeyFileLoader.Load(Path.Combine(testData, "keys.example.clear"));
        List<RadioAlias> aliases = AliasFileLoader.Load(Path.Combine(testData, "alias.example.yml"));

        Assert.Equal(4, keys.Keys.Count);
        Assert.Equal((ushort)1, keys.Keys[0].KeyId);
        Assert.Equal("p25", keys.Keys[0].Protocol);
        Assert.Equal("dmr", keys.Keys[2].Protocol);
        Assert.Equal("nxdn", keys.Keys[3].Protocol);
        Assert.Single(aliases);
        Assert.Equal("Radio 1", AliasFileLoader.FindAlias(aliases, 1));
    }

    [Fact]
    public void TypedKeyCodecAcceptsLegacyHexAndRoundTripsAllFields()
    {
        KeyContainer parsed = KeyFileLoader.Parse("""
            keys:
              - system: Regional FNE
                protocol: dmr
                keyId: 0x2A
                algId: 0x05
                key: 001122AABB
                vendorExtension: ignored
            """);

        KeyEntry key = Assert.Single(parsed.Keys);
        Assert.Equal("Regional FNE", key.System);
        Assert.Equal("dmr", key.Protocol);
        Assert.Equal((ushort)0x2A, key.KeyId);
        Assert.Equal(0x05, key.AlgId);
        Assert.Equal("001122AABB", key.Key);

        KeyContainer roundTrip = KeyFileLoader.Parse(KeyFileLoader.Serialize(parsed));
        KeyEntry saved = Assert.Single(roundTrip.Keys);
        Assert.Equal(key.Protocol, saved.Protocol);
        Assert.Equal(key.System, saved.System);
        Assert.Equal(key.KeyId, saved.KeyId);
        Assert.Equal(key.AlgId, saved.AlgId);
        Assert.Equal(key.Key, saved.Key);
    }

    [Fact]
    public void KeyCodecWritesOnlyTheCompactPublicSchema()
    {
        var keys = new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    Name = "Dispatch AES",
                    System = "Regional P25",
                    Protocol = "p25",
                    KeyId = 0x2A,
                    AlgId = 0x84,
                    Key = "00112233"
                },
                new KeyEntry
                {
                    Name = "Operations Basic Privacy",
                    System = "Regional DMR",
                    Protocol = "dmr",
                    KeyId = 0x03,
                    AlgId = 0x01,
                    Key = "A1B2C3D4E5"
                }
            ]
        };

        string yaml = KeyFileLoader.Serialize(keys);

        Assert.Contains("name: Dispatch AES", yaml, StringComparison.Ordinal);
        Assert.Contains("system: Regional P25", yaml, StringComparison.Ordinal);
        Assert.Contains("keyId: 0x2A", yaml, StringComparison.Ordinal);
        Assert.Contains("algId: 0x84", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: p25", yaml, StringComparison.Ordinal);
        Assert.Contains("protocol: dmr", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("requiredLength", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("displayName", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyBytes", yaml, StringComparison.OrdinalIgnoreCase);
        string[] serializedFields = yaml.Split('\n')
            .Select(line => line.Trim().TrimStart('-').Trim())
            .Where(line => line.Contains(':', StringComparison.Ordinal) && line != "keys:")
            .Select(line => line[..line.IndexOf(':')])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["name", "system", "protocol", "keyId", "algId", "key"], serializedFields);

        KeyEntry[] roundTrip = KeyFileLoader.Parse(yaml).Keys.ToArray();
        Assert.Equal("Dispatch AES", roundTrip[0].Name);
        Assert.Equal("Regional P25", roundTrip[0].System);
        Assert.Equal("p25", roundTrip[0].Protocol);
        Assert.Equal("Operations Basic Privacy", roundTrip[1].Name);
        Assert.Equal("Regional DMR", roundTrip[1].System);
        Assert.Equal("dmr", roundTrip[1].Protocol);
    }

    [Fact]
    public void KeyValidationAllowsMatchingIdentitiesInDifferentSystemScopes()
    {
        var keys = new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    System = "System A",
                    Protocol = "dmr",
                    KeyId = 1,
                    AlgId = 1,
                    Key = "0102030405"
                },
                new KeyEntry
                {
                    System = "System B",
                    Protocol = "dmr",
                    KeyId = 1,
                    AlgId = 1,
                    Key = "A1A2A3A4A5"
                }
            ]
        };

        Assert.Empty(KeyFileValidator.Validate(keys));
    }

    [Fact]
    public void TypedAliasCodecHandlesEmptyFilesAndRoundTripsAliases()
    {
        Assert.Empty(AliasFileLoader.Parse(string.Empty));

        var aliases = new List<RadioAlias>
        {
            new() { Alias = "true", Rid = 42 },
            new() { Alias = "Medic 7", Rid = 7 }
        };

        string yaml = AliasFileLoader.Serialize(aliases);
        List<RadioAlias> roundTrip = AliasFileLoader.Parse(yaml);

        Assert.Contains("alias: 'true'", yaml, StringComparison.Ordinal);
        Assert.Equal(2, roundTrip.Count);
        Assert.Equal("true", roundTrip[0].Alias);
        Assert.Equal((uint)42, roundTrip[0].Rid);
        Assert.Equal("Medic 7", roundTrip[1].Alias);
    }

    [Fact]
    public void LoadsOptionalSystemAliasesRelativeToTheCodeplug()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-alias-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string codeplugPath = Path.Combine(root, "codeplug.yml");
        string aliasPath = Path.Combine(root, "aliases.yml");

        File.WriteAllText(codeplugPath, """
            systems:
              - name: "System 1"
                address: "127.0.0.1"
                port: 62031
                aliasPath: "aliases.yml"
            zones:
              - name: "Dispatch"
                channels:
                  - name: "Channel 1"
                    system: "System 1"
                    tgid: "100"
                    mode: "analog"
            """);
        File.WriteAllText(aliasPath, """
            - alias: "Unit 42"
              rid: 42
            """);

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(codeplugPath);

            Assert.Equal("Unit 42", AliasFileLoader.FindAlias(configuration.Systems[0].RidAlias, 42));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidatesWebStreamNamesAndUrls()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration { Name = "System 1", Address = "127.0.0.1", Port = 62031 }
            ],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Dispatch",
                    WebStreams =
                    [
                        new WebStreamConfiguration { Name = "Dispatch stream", Url = "https://example.test/live" },
                        new WebStreamConfiguration { Name = "Dispatch stream", Url = "file:///tmp/audio" }
                    ]
                }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("Web stream name 'Dispatch stream' is duplicated.", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must use an absolute HTTP or HTTPS URL", StringComparison.Ordinal));
    }

    [Fact]
    public void ScopesDuplicateChannelNamesToTheirConfiguredSystem()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration { Name = "Alpha", Address = "127.0.0.1", Port = 62031 },
                new SystemConfiguration { Name = "Beta", Address = "127.0.0.2", Port = 62032 }
            ],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Dispatch",
                    Channels =
                    [
                        new ChannelConfiguration { Name = "Dispatch", System = "Alpha", Tgid = "100", Mode = "analog" },
                        new ChannelConfiguration { Name = "Dispatch", System = "Beta", Tgid = "200", Mode = "analog" },
                        new ChannelConfiguration { Name = "Dispatch", System = "Alpha", Tgid = "101", Mode = "analog" }
                    ]
                }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Single(errors);
        Assert.Contains("duplicated in system 'Alpha'", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MergesAndNormalizesCurrentAndLegacyGroups()
    {
        var configuration = new ConsoleConfiguration
        {
            Groups =
            [
                new GroupConfiguration { Name = "Patch 1", Type = "PATCH" },
                new GroupConfiguration { Name = "Multi", Type = "multiselect" }
            ],
            LegacyPatchGroups =
            [
                new GroupConfiguration { Name = " patch 1 ", Type = string.Empty },
                new GroupConfiguration { Name = "Legacy", Type = string.Empty }
            ]
        };

        configuration.NormalizeGroups();

        Assert.Equal(["Patch 1", "Multi", "Legacy"], configuration.Groups.Select(group => group.Name));
        Assert.Equal(["patch", "multiselect", "patch"], configuration.Groups.Select(group => group.Type));
        Assert.Empty(configuration.LegacyPatchGroups);
        Assert.True(configuration.Groups[0].IsPatchGroup());
        Assert.True(configuration.Groups[1].IsMultiselectGroup());
    }

    [Fact]
    public void ConvertsCodeplugDmrSlotToZeroBasedRuntimeSlot()
    {
        var channel = new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "DMR",
            Slot = 2,
            RxOnly = true
        };

        ChannelRuntimeDefinition definition = ChannelRuntimeDefinition.FromConfiguration(channel);

        Assert.Equal("dmr", definition.Mode);
        Assert.Equal(ChannelProtocol.Dmr, definition.Protocol);
        Assert.Equal((byte)1, definition.Slot);
        Assert.Equal((uint)99, definition.DestinationId);
        Assert.True(definition.RxOnly);
    }

    [Theory]
    [InlineData("analog", ChannelProtocol.Analog)]
    [InlineData("DMR", ChannelProtocol.Dmr)]
    [InlineData(" p25 ", ChannelProtocol.P25)]
    [InlineData("NxDn", ChannelProtocol.Nxdn)]
    public void ParsesChannelProtocolOnceWithoutChangingNormalizedMode(
        string mode,
        ChannelProtocol expected)
    {
        var definition = new ChannelRuntimeDefinition("Dispatch", "System 1", mode, 99, 0);

        Assert.Equal(expected, definition.Protocol);
        Assert.Equal(mode.Trim().ToLowerInvariant(), definition.Mode);
    }

    [Fact]
    public void CarriesChannelEncryptionPolicyIntoRuntime()
    {
        var channel = new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "AES",
            KeyId = "0x50",
            SelectableEncryption = true
        };

        ChannelRuntimeDefinition definition = ChannelRuntimeDefinition.FromConfiguration(channel);

        Assert.Equal("aes", definition.EncryptionAlgorithm);
        Assert.Equal("0x50", definition.EncryptionKeyId);
        Assert.True(definition.SelectableEncryption);
        Assert.True(definition.IsEncrypted);
    }

    [Fact]
    public void RuntimePublishesReceivingAndIdleState()
    {
        var runtime = new ChannelRuntime(new ChannelRuntimeDefinition("Dispatch", "System 1", "p25", 99, 0));
        var changed = new List<string>();
        runtime.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        runtime.MarkReceiving(123, 456, DateTimeOffset.UnixEpoch);

        Assert.Equal(ChannelRuntimeState.Receiving, runtime.State);
        Assert.Equal((uint)123, runtime.SourceId);
        Assert.Equal((uint)456, runtime.StreamId);
        Assert.Contains(nameof(ChannelRuntime.StateText), changed);
        Assert.Contains("Receiving from 123", runtime.StateText);

        changed.Clear();
        runtime.MarkReceiving(123, 456, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal([nameof(ChannelRuntime.LastActivity)], changed);

        runtime.MarkIdle(DateTimeOffset.UnixEpoch);

        Assert.Equal(ChannelRuntimeState.Idle, runtime.State);
        Assert.Null(runtime.SourceId);
        Assert.Null(runtime.StreamId);
    }

    [Fact]
    public void RuntimeRejectsInvalidDmrSlot()
    {
        var channel = new ChannelConfiguration
        {
            Name = "Invalid",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 3
        };

        Assert.Throws<InvalidDataException>(() => ChannelRuntimeDefinition.FromConfiguration(channel));
    }

    [Fact]
    public void ValidationAcceptsAnalogChannelMode()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration
                {
                    Name = "System 1",
                    Address = "127.0.0.1",
                    Port = 62031
                }
            ],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Analog",
                    Channels =
                    [
                        new ChannelConfiguration
                        {
                            Name = "Analog Dispatch",
                            System = "System 1",
                            Tgid = "100",
                            Mode = "analog"
                        }
                    ]
                }
            ]
        };

        Assert.Empty(ConfigurationLoader.Validate(configuration));
    }

    [Fact]
    public void ValidationRejectsInvalidChannelDestinationAndDmrSlot()
    {
        var configuration = new ConsoleConfiguration
        {
            Systems =
            [
                new SystemConfiguration
                {
                    Name = "System 1",
                    Address = "127.0.0.1",
                    Port = 62031
                }
            ],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Primary",
                    Channels =
                    [
                        new ChannelConfiguration
                        {
                            Name = "Invalid",
                            System = "System 1",
                            Tgid = "not-a-number",
                            Mode = "dmr",
                            Slot = 3
                        }
                    ]
                }
            ]
        };

        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("non-zero numeric destination ID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must use slot 1 or 2", StringComparison.Ordinal));
    }
}
