// SPDX-License-Identifier: AGPL-3.0-only
/**
* YAML schema/fixture gate for the linked production dvmconsole/Codeplug.cs.
* The compile-smoke contract tests (CodeplugContractTests.cs) pin enum values,
* defaults, and helper behavior; this file pins the on-disk YAML schema:
* camelCase naming, snake_case aliases (web_streams, rx_only,
* selectable_encryption, card_size), the legacy patchGroups key, and the
* production deserializer configuration (CamelCaseNamingConvention.Instance +
* IgnoreUnmatchedProperties, MainWindow.xaml.cs LoadCodeplug lines 840-843).
*
* The fixture (Fixtures/minimal_codeplug.yml) is copied to the test output
* directory by the project file and loaded via AppContext.BaseDirectory, so
* the suite stays deterministic and headless.
*/
using dvmconsole;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Schema round-trip tests against the deterministic fixture.
    /// </summary>
    public class CodeplugSchemaTests
    {
        /// <summary>
        /// Absolute path of the fixture copied to the test output directory.
        /// </summary>
        private static readonly string FixturePath =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal_codeplug.yml");

        /// <summary>
        /// Mirrors the exact deserializer configuration used by production
        /// LoadCodeplug (MainWindow.xaml.cs lines 840-843).
        /// </summary>
        private static IDeserializer CreateProductionDeserializer()
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        private static Codeplug Deserialize(string yaml)
        {
            return CreateProductionDeserializer().Deserialize<Codeplug>(yaml);
        }

        private static Codeplug LoadFixture()
        {
            return Deserialize(File.ReadAllText(FixturePath));
        }

        /* Fixture availability */

        /// <summary>
        /// The fixture must be present next to the test assembly; the csproj
        /// copies it there with CopyToOutputDirectory=PreserveNewest.
        /// </summary>
        [Fact]
        public void Fixture_Exists_InTestOutputDirectory()
        {
            Assert.True(File.Exists(FixturePath), $"Fixture missing from test output: {FixturePath}");
        }

        /// <summary>
        /// The fixture deserializes under the exact production configuration,
        /// including tolerance of unknown keys (IgnoreUnmatchedProperties).
        /// </summary>
        [Fact]
        public void Fixture_Deserializes_WithProductionConfiguration()
        {
            var codeplug = LoadFixture();

            Assert.NotNull(codeplug);
            Assert.NotNull(codeplug.Systems);
            Assert.NotNull(codeplug.Zones);
            Assert.NotNull(codeplug.Groups);
            Assert.NotNull(codeplug.LegacyPatchGroups);
            Assert.True(codeplug.PatchSourceIdPassthrough);
        }

        /* Systems */

        /// <summary>
        /// CamelCase system fields and the ridAlias list survive deserialization.
        /// </summary>
        [Fact]
        public void System_Fields_SurviveDeserialization()
        {
            var system = Assert.Single(LoadFixture().Systems);

            Assert.Equal("Simplex Repeater", system.Name);
            Assert.Equal("310001", system.Identity);
            Assert.Equal("127.0.0.1", system.Address);
            Assert.Equal(31000, system.Port);
            Assert.False(system.Encrypted);
            Assert.Equal(310000u, system.PeerId);
            Assert.Equal("3100001", system.Rid);
            Assert.Equal("./fixtures/alias.yml", system.AliasPath);

            var alias = Assert.Single(system.RidAlias);
            Assert.Equal("Test Radio", alias.Alias);
            Assert.Equal(3100001, alias.Rid);
        }

        /* Zones / channels */

        /// <summary>
        /// Zone and channel camelCase fields survive deserialization.
        /// </summary>
        [Fact]
        public void Zone_And_Channel_Fields_SurviveDeserialization()
        {
            var zone = Assert.Single(LoadFixture().Zones);

            Assert.Equal("Zone A", zone.Name);
            Assert.Equal("#1a2b3c", zone.TabColor);
            Assert.Equal("#ffffff", zone.TabTextColor);

            var channel = zone.Channels.Single(c => c.Name == "CH 1 Aliased Flags");
            Assert.Equal("Simplex Repeater", channel.System);
            Assert.Equal("31001", channel.Tgid);
            Assert.Equal(1, channel.Slot);
            Assert.Equal("dmr", channel.Mode);
            Assert.Equal("none", channel.Algo);
        }

        /* web_streams alias */

        /// <summary>
        /// The snake_case web_streams key (ApplyNamingConventions=false alias)
        /// maps to the Zone.WebStreams list.
        /// </summary>
        [Fact]
        public void WebStreams_SnakeCaseAlias_MapsToWebStreamList()
        {
            var zone = Assert.Single(LoadFixture().Zones);

            var stream = Assert.Single(zone.WebStreams);
            Assert.Equal("Local Feed", stream.Name);
            Assert.Equal("http://127.0.0.1:8080/stream.mp3", stream.Url);
            Assert.Equal("feed-user", stream.AuthUsername);
            Assert.Equal("feed-placeholder", stream.AuthPassword);
            Assert.Equal("#00cc44", stream.IdleColor);
        }

        /* Channel snake_case aliases */

        /// <summary>
        /// The snake_case aliases rx_only, selectable_encryption, and card_size
        /// (all ApplyNamingConventions=false) map to their Channel properties.
        /// </summary>
        [Fact]
        public void Channel_SnakeCaseAliases_MapToRxOnlySelectableEncryptionCardSize()
        {
            var zone = Assert.Single(LoadFixture().Zones);
            var channel = zone.Channels.Single(c => c.Name == "CH 1 Aliased Flags");

            Assert.True(channel.RxOnly);
            Assert.True(channel.SelectableEncryption);
            Assert.Equal("large", channel.CardSize);
        }

        /* patchGroups + NormalizeGroups */

        /// <summary>
        /// The legacy patchGroups key deserializes into LegacyPatchGroups and
        /// NormalizeGroups merges, dedupes (current group wins the name
        /// collision), trims, and lowercases types.
        /// </summary>
        [Fact]
        public void PatchGroups_Deserialize_And_NormalizeGroups_MergesDedupesNormalizes()
        {
            var codeplug = LoadFixture();

            // Legacy patchGroups key deserializes into LegacyPatchGroups.
            Assert.Equal(2, codeplug.LegacyPatchGroups.Count);
            Assert.Equal("Tac 1", codeplug.LegacyPatchGroups[0].Name);
            Assert.Equal("", codeplug.LegacyPatchGroups[0].Type); // explicit blank type
            Assert.Equal("Legacy Patch", codeplug.LegacyPatchGroups[1].Name);
            Assert.Equal("  PATCH  ", codeplug.LegacyPatchGroups[1].Type);

            codeplug.NormalizeGroups();

            // Merged (current groups first), deduped on the "Tac 1" collision,
            // normalized: blank type -> "patch", "  PATCH  " -> "patch",
            // "multiselect" preserved.
            Assert.Equal(new[] { "Dispatch", "Tac 1", "Legacy Patch" }, codeplug.Groups.Select(g => g.Name));
            Assert.Equal("multiselect", codeplug.Groups.Single(g => g.Name == "Dispatch").Type);
            Assert.Equal("patch", codeplug.Groups.Single(g => g.Name == "Tac 1").Type);
            Assert.Equal("patch", codeplug.Groups.Single(g => g.Name == "Legacy Patch").Type);
        }

        /* Safe defaults */

        /// <summary>
        /// Keys omitted from a codeplug YAML leave the safe property
        /// initializers intact: CardSize "normal", RxOnly/SelectableEncryption
        /// false, Mode "p25", Algo "none", and null/zero/false elsewhere.
        /// </summary>
        [Fact]
        public void ChannelDefaults_Remain_WhenKeysOmitted()
        {
            const string snippet = @"
systems:
  - name: Minimal
zones:
  - name: Z
    channels:
      - name: Plain CH
";
            var codeplug = Deserialize(snippet);
            var channel = codeplug.Zones[0].Channels[0];

            Assert.Equal("normal", channel.CardSize);
            Assert.False(channel.RxOnly);
            Assert.False(channel.SelectableEncryption);
            Assert.Equal("p25", channel.Mode);
            Assert.Equal("none", channel.Algo);
            Assert.Null(channel.KeyId);
            Assert.Equal(0, channel.Slot);
            Assert.Null(codeplug.KeyFile);
            Assert.False(codeplug.PatchSourceIdPassthrough);
        }
    }
}
