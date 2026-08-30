using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationDocumentTests
{
    private const string InteroperableFixture = """
        keyFile: ./keys.clear
        patchSourceIdPassthrough: true
        encryptionKey: legacy-root-value
        systems:
          - name: North
            identity: Console
            address: 192.0.2.10
            port: 62031
            peerId: 1001
            rid: "2001"
            password: deployment-password
            encrypted: true
            presharedKey: 00112233445566778899AABBCCDDEEFF
            kmfPresharedKey: FFEEDDCCBBAA99887766554433221100
            transportEncryptionMode: auto
            aliasPath: ./aliases.yml
            vendorExtension: retained-system-value
        zones:
          - name: Dispatch
            tabColor: "#244E73"
            channels:
              - name: Primary
                system: North
                tgid: "101"
                mode: p25
                slot: 1
                algo: aes
                keyId: "1"
                rx_only: false
                selectable_encryption: true
                card_size: normal
                resourceColor: "#244E73"
                channelExtension: retained-channel-value
            web_streams:
              - name: Backup audio
                url: https://stream.example.test/audio
                authUsername: operator
                authPassword: stream-password
                idleColor: "#334455"
        patchGroups:
          - name: Dispatch Patch
            type: patch
        """;

    [Fact]
    public void RoundTripPreservesUnknownFieldsAndCanonicalizesLegacyGroups()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(InteroperableFixture, "/tmp/studio-codeplug.yml");

        document.Configuration.Zones[0].Channels[0].Name = "Primary Dispatch";
        document.MarkDirty();
        string serialized = document.Serialize();

        Assert.Contains("encryptionKey: legacy-root-value", serialized, StringComparison.Ordinal);
        Assert.Contains("vendorExtension: retained-system-value", serialized, StringComparison.Ordinal);
        Assert.Contains("channelExtension: retained-channel-value", serialized, StringComparison.Ordinal);
        Assert.Contains("name: Primary Dispatch", serialized, StringComparison.Ordinal);
        Assert.Contains("groups:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("memberships:", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(document.UnknownFields, field => field.Name == "encryptionKey");
    }

    [Fact]
    public void UnknownChannelFieldsFollowTheirChannelWhenRowsAreReordered()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse("""
            systems:
              - name: North
                address: 127.0.0.1
                port: 62031
            zones:
              - name: Dispatch
                channels:
                  - name: Primary
                    system: North
                    tgid: '101'
                    mode: p25
                    card_size: normal
                    channelExtension: retained-channel-value
                  - name: Secondary
                    system: North
                    tgid: '102'
                    mode: p25
                    card_size: normal
                    channelExtension: secondary-value
            groups: []
            """);

        List<ChannelConfiguration> channels = document.Configuration.Zones[0].Channels;
        (channels[0], channels[1]) = (channels[1], channels[0]);

        string serialized = document.Serialize();
        int secondary = serialized.IndexOf("name: Secondary", StringComparison.Ordinal);
        int primary = serialized.IndexOf("name: Primary", StringComparison.Ordinal);
        int secondaryExtension = serialized.IndexOf("channelExtension: secondary-value", StringComparison.Ordinal);
        int primaryExtension = serialized.IndexOf("channelExtension: retained-channel-value", StringComparison.Ordinal);

        Assert.True(secondary < secondaryExtension && secondaryExtension < primary);
        Assert.True(primary < primaryExtension);
    }

    [Fact]
    public void InsertingChannelDoesNotCopyUnknownFieldsFromTheDisplacedRow()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse("""
            systems:
              - name: North
                address: 127.0.0.1
                port: 62031
            zones:
              - name: Dispatch
                channels:
                  - name: Primary
                    system: North
                    tgid: '101'
                    mode: p25
                    card_size: normal
                    vendorFlag: retained-only-on-primary
            groups: []
            """);

        document.Configuration.Zones[0].Channels.Insert(0, new ChannelConfiguration
        {
            Name = "Inserted",
            System = "North",
            Tgid = "102",
            Mode = "p25",
            CardSize = "normal"
        });

        string serialized = document.Serialize();
        int inserted = serialized.IndexOf("name: Inserted", StringComparison.Ordinal);
        int primary = serialized.IndexOf("name: Primary", StringComparison.Ordinal);
        int vendor = serialized.IndexOf("vendorFlag: retained-only-on-primary", StringComparison.Ordinal);

        Assert.True(inserted >= 0 && inserted < primary);
        Assert.True(primary < vendor);
        Assert.Equal(vendor, serialized.LastIndexOf("vendorFlag: retained-only-on-primary", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyKnownCollectionsAreNotReportedAsUnknownFields()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse("""
            systems: []
            zones: []
            groups: []
            """);

        Assert.Empty(document.UnknownFields);
    }

    [Fact]
    public void AnchorsOpenReadOnlyInsteadOfBeingRewritten()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse("""
            systems: &systems
              - name: North
                address: 127.0.0.1
                port: 62031
            zones: []
            groups: []
            """);

        Assert.True(document.IsReadOnly);
        Assert.Contains("anchors", document.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => document.Serialize());
    }

    [Fact]
    public void DuplicateMappingKeysOpenReadOnlyInsteadOfBeingRewritten()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse("""
            systems:
              - name: North
                name: Duplicate
                address: 127.0.0.1
                port: 62031
            zones: []
            groups: []
            """);

        Assert.True(document.IsReadOnly);
        Assert.Contains("duplicate", document.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => document.Serialize());
    }

    [Fact]
    public void SanitizedExportRemovesSecretsAddressesUrlsAndIdentifiers()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(InteroperableFixture);

        string sanitized = document.SerializeSanitized();

        Assert.DoesNotContain("deployment-password", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("stream-password", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("stream.example.test", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("tgid: '101'", sanitized, StringComparison.Ordinal);
        Assert.Contains("redacted.invalid", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveTransactionDetectsExternalChangesAndCreatesRestrictedBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-studio-tests", Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "codeplug.yml");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        try
        {
            const string valid = "systems:\n  - name: Test\n    address: 127.0.0.1\n    port: 62031\nzones: []\ngroups: []\n";
            File.WriteAllText(target, "systems: []\n");
            string hash = ConfigurationDocument.ComputeFileHash(target);
            File.WriteAllText(target, "systems: []\n# external edit\n");
            var conflictPlan = new ConfigurationSavePlan(
                [new ConfigurationFileChange(target, valid, hash, "Codeplug", true)],
                []);

            Assert.Throws<ConfigurationExternalChangeException>(() =>
                ConfigurationSaveTransaction.Execute(conflictPlan, backups));

            string currentHash = ConfigurationDocument.ComputeFileHash(target);
            var savePlan = new ConfigurationSavePlan(
                [new ConfigurationFileChange(target, valid, currentHash, "Codeplug", true)],
                []);
            ConfigurationSaveResult result = ConfigurationSaveTransaction.Execute(savePlan, backups);

            Assert.Equal(valid, File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(result.BackupDirectory));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(target));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveTransactionRejectsCollidingArtifactTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-studio-collision-tests", Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "codeplug.yml");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        try
        {
            var plan = new ConfigurationSavePlan(
            [
                new ConfigurationFileChange(target, "systems: []\n", null, "Codeplug", true),
                new ConfigurationFileChange(target, "keys: []\n", null, "Encryption key file", true)
            ],
            []);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => ConfigurationSaveTransaction.Execute(plan, backups));

            Assert.Contains("more than one artifact", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveTransactionRollsBackAnEarlierReplacementWhenALaterWriteFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-studio-rollback-tests", Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(root, "codeplug.yml");
        string invalidDestination = Path.Combine(root, "alias-target");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(invalidDestination);
        try
        {
            const string original = "systems: []\n";
            const string replacement = "systems:\n  - name: Test\n    address: 127.0.0.1\n    port: 62031\nzones: []\ngroups: []\n";
            File.WriteAllText(codeplugPath, original);
            var plan = new ConfigurationSavePlan(
            [
                new ConfigurationFileChange(
                    codeplugPath,
                    replacement,
                    ConfigurationDocument.ComputeFileHash(codeplugPath),
                    "Codeplug",
                    true),
                new ConfigurationFileChange(
                    invalidDestination,
                    "[]\n",
                    null,
                    "RID alias file",
                    false)
            ],
            []);

            Exception failure = Assert.ThrowsAny<Exception>(
                () => ConfigurationSaveTransaction.Execute(plan, backups));
            Assert.True(
                failure is IOException or UnauthorizedAccessException,
                $"Expected a file-system failure, but received {failure.GetType().FullName}.");
            Assert.Equal(original, File.ReadAllText(codeplugPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyGroupStateMigratesAndSaveAsCopiesWithoutSharingMutableState()
    {
        string original = Path.Combine(Path.GetTempPath(), "original-codeplug.yml");
        string copy = Path.Combine(Path.GetTempPath(), "copy-codeplug.yml");
        var settings = new UserSettings
        {
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch"] = [new PatchMemberSetting { SystemName = "North", DestinationId = 101 }]
            },
            PatchGroupModes = new Dictionary<string, bool> { ["Dispatch"] = true },
            PatchGroupEnabledStates = new Dictionary<string, bool> { ["Dispatch"] = true }
        };

        CodeplugGroupState migrated = CodeplugGroupStateStore.GetOrMigrate(settings, original);
        string unrelated = Path.Combine(Path.GetTempPath(), "unrelated-codeplug.yml");
        CodeplugGroupState isolated = CodeplugGroupStateStore.GetOrMigrate(settings, unrelated);
        CodeplugGroupState copied = CodeplugGroupStateStore.CopyForSaveAs(settings, original, copy);
        copied.Memberships["Dispatch"][0].DestinationId = 202;

        Assert.Equal((uint)101, migrated.Memberships["Dispatch"][0].DestinationId);
        Assert.Equal((uint)202, copied.Memberships["Dispatch"][0].DestinationId);
        Assert.True(migrated.OneWayModes["Dispatch"]);
        Assert.True(migrated.EnabledStates["Dispatch"]);
        Assert.True(settings.PatchGroupMemberships.ContainsKey("Dispatch"));
        Assert.Empty(isolated.Memberships);
        Assert.Equal(3, settings.CodeplugGroupStates.Count);
    }

    [Fact]
    public void CodeplugGroupStateSurvivesSettingsStoreRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-group-settings-tests", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(root, "UserSettings.json");
        string codeplugPath = Path.Combine(root, "codeplug.yml");
        try
        {
            var store = new UserSettingsStore(settingsPath);
            var settings = new UserSettings();
            CodeplugGroupState state = CodeplugGroupStateStore.GetOrMigrate(settings, codeplugPath);
            state.Memberships["Dispatch"] =
            [
                new PatchMemberSetting { SystemName = "North", DestinationId = 101 }
            ];
            store.Save(settings);

            UserSettings loaded = store.Load();
            CodeplugGroupState restored = CodeplugGroupStateStore.GetOrMigrate(loaded, codeplugPath);

            Assert.Single(restored.Memberships["Dispatch"]);
            Assert.Equal((uint)101, restored.Memberships["Dispatch"][0].DestinationId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetailedValidationUsesNormalCardSizeAndReportsWarningsSeparately()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(InteroperableFixture.Replace(
            "https://stream.example.test/audio",
            "http://stream.example.test/audio",
            StringComparison.Ordinal));

        IReadOnlyList<ConfigurationValidationIssue> issues = document.Validate();

        Assert.DoesNotContain(issues, issue => issue.IsError);
        Assert.Contains(issues, issue =>
            issue.Severity == ConfigurationValidationSeverity.Warning &&
            issue.Domain == "Web Streams");
        Assert.Equal("normal", document.Configuration.Zones[0].Channels[0].CardSize);
    }

    [Fact]
    public void EncryptionAlgorithmCatalogUsesProtocolSpecificIdentifiers()
    {
        EncryptionAlgorithmOption p25Aes = Assert.Single(
            EncryptionAlgorithmCatalog.ForKeyProtocol("p25"), option => option.DisplayName == "AES-256");
        EncryptionAlgorithmOption dmrAes = Assert.Single(
            EncryptionAlgorithmCatalog.ForKeyProtocol("dmr"), option => option.DisplayName == "AES-256");
        EncryptionAlgorithmOption nxdnEhr = Assert.Single(
            EncryptionAlgorithmCatalog.ForKeyProtocol("nxdn"), option => option.DisplayName == "EHR");

        Assert.Equal(0x84, p25Aes.AlgorithmId);
        Assert.Equal(0x05, dmrAes.AlgorithmId);
        Assert.Equal(0x01, nxdnEhr.AlgorithmId);
        Assert.Equal("0x84", p25Aes.AlgorithmIdText);
        Assert.Equal("P25 Phase 1", ConfigurationProtocolCatalog.DisplayName("p25"));
        Assert.Equal("p25", Assert.Single(
            ConfigurationProtocolCatalog.ForChannels,
            option => option.DisplayName == "P25 Phase 1").Value);
    }

    [Fact]
    public void ChannelEncryptionValidationIsModeAwareAndAcceptsPrefixedHexKeyIds()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(InteroperableFixture);
        ChannelConfiguration channel = document.Configuration.Zones[0].Channels[0];
        channel.Mode = "dmr";
        channel.Algo = "ehr";
        channel.KeyId = "0x2A";

        IReadOnlyList<ConfigurationValidationIssue> invalidAlgorithm = document.Validate();

        Assert.Contains(invalidAlgorithm, issue =>
            issue.Path == "zones[0].channels[0].algo" &&
            issue.Message.Contains("not available for DMR", StringComparison.Ordinal));

        channel.Algo = "aes";
        IReadOnlyList<ConfigurationValidationIssue> validEncryption = document.Validate();

        Assert.DoesNotContain(validEncryption, issue =>
            issue.Path is "zones[0].channels[0].algo" or "zones[0].channels[0].keyId");
        Assert.True(EncryptionAlgorithmCatalog.TryParseChannelKeyId("dmr", "0x2A", out ushort keyId));
        Assert.Equal((ushort)0x2A, keyId);
    }

    [Fact]
    public void P25Phase1ChannelsDoNotValidateOrRequireDmrSlots()
    {
        ConfigurationDocument document = ConfigurationDocument.Parse(InteroperableFixture);
        ChannelConfiguration channel = document.Configuration.Zones[0].Channels[0];
        channel.Mode = "p25";
        channel.Slot = 99;

        IReadOnlyList<ConfigurationValidationIssue> issues = document.Validate();

        Assert.DoesNotContain(issues, issue => issue.Path == "zones[0].channels[0].slot");
    }

    [Fact]
    public void KeyValidationAppliesProtocolRulesWithoutIncludingMaterialInDiagnostics()
    {
        const string secret = "DEADBEEF";
        var keys = new KeyContainer
        {
            Keys =
            [
                new KeyEntry { Protocol = "p25", AlgId = 0x84, KeyId = 1, Key = "01" },
                new KeyEntry { Protocol = "dmr", AlgId = 0x05, KeyId = 2, Key = secret },
                new KeyEntry { Protocol = "nxdn", AlgId = 0x01, KeyId = 64, Key = "0000" }
            ]
        };

        IReadOnlyList<ConfigurationValidationIssue> issues = KeyFileValidator.Validate(keys);

        Assert.DoesNotContain(issues, issue => issue.Path == "keys[0].key");
        Assert.Contains(issues, issue => issue.Path == "keys[1].key" && issue.Message.Contains("32 bytes", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "keys[2].keyId" && issue.Message.Contains("1 and 63", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "keys[2].key" && issue.Message.Contains("non-zero 15-bit seed", StringComparison.Ordinal));
        Assert.All(issues, issue => Assert.DoesNotContain(secret, issue.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LargeSyntheticDeploymentShapeValidatesWithoutErrors()
    {
        var configuration = new ConsoleConfiguration();
        for (int systemIndex = 0; systemIndex < 5; systemIndex++)
        {
            configuration.Systems.Add(new SystemConfiguration
            {
                Name = $"System {systemIndex + 1}",
                Address = $"192.0.2.{systemIndex + 1}",
                Port = 62031 + systemIndex,
                PeerId = (uint)(1000 + systemIndex),
                Rid = (2000 + systemIndex).ToString()
            });
        }

        int channelNumber = 0;
        for (int zoneIndex = 0; zoneIndex < 11; zoneIndex++)
        {
            var zone = new ZoneConfiguration { Name = $"Zone {zoneIndex + 1}" };
            int channelsInZone = zoneIndex < 10 ? 14 : 13;
            for (int index = 0; index < channelsInZone; index++)
            {
                int systemIndex = channelNumber % configuration.Systems.Count;
                zone.Channels.Add(new ChannelConfiguration
                {
                    Name = $"Channel {channelNumber + 1}",
                    System = configuration.Systems[systemIndex].Name,
                    Tgid = (1001 + channelNumber).ToString(),
                    Mode = channelNumber % 3 == 0 ? "dmr" : "p25",
                    Slot = channelNumber % 2 + 1,
                    CardSize = channelNumber % 3 == 0 ? "small" : channelNumber % 3 == 1 ? "normal" : "large",
                    ResourceColor = "#244E73"
                });
                channelNumber++;
            }
            if (zoneIndex % 3 == 0)
            {
                zone.WebStreams.Add(new WebStreamConfiguration
                {
                    Name = $"Stream {zoneIndex + 1}",
                    Url = $"https://stream-{zoneIndex + 1}.example.test/audio"
                });
            }
            configuration.Zones.Add(zone);
        }
        configuration.Groups.Add(new GroupConfiguration { Name = "Dispatch Patch", Type = "patch" });

        IReadOnlyList<ConfigurationValidationIssue> issues = ConfigurationValidator.ValidateDetailed(configuration);

        Assert.Equal(5, configuration.Systems.Count);
        Assert.Equal(11, configuration.Zones.Count);
        Assert.Equal(153, configuration.Zones.Sum(zone => zone.Channels.Count));
        Assert.DoesNotContain(issues, issue => issue.IsError);
    }
}
