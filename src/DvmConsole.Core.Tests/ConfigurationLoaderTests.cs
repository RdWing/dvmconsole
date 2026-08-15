using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationLoaderTests
{
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

        Assert.Equal(2, keys.Keys.Count);
        Assert.Equal((ushort)1, keys.Keys[0].KeyId);
        Assert.Single(aliases);
        Assert.Equal("Radio 1", AliasFileLoader.FindAlias(aliases, 1));
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
        Assert.Equal((byte)1, definition.Slot);
        Assert.Equal((uint)99, definition.DestinationId);
        Assert.True(definition.RxOnly);
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
