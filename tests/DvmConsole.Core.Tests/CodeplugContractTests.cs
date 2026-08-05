// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic compile-smoke contract tests for the production
* DvmConsole.Core/Configuration/Codeplug.cs. These assert stable serialization-facing surface
* (enum values, defaults, helper behavior) that a codeplug YAML round-trip
* depends on. YAML fixture/schema round-trip tests are a separate gate and
* intentionally not part of this scaffold.
*/
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="Codeplug"/>.
    /// </summary>
    public class CodeplugContractTests
    {
        /// <summary>
        /// The ChannelMode enum values are part of the on-disk codeplug schema:
        /// DMR=0, NXDN=1, P25=2. They must never be renumbered.
        /// </summary>
        [Fact]
        public void ChannelMode_EnumValues_AreStableContract()
        {
            Assert.Equal(0, (int)Codeplug.ChannelMode.DMR);
            Assert.Equal(1, (int)Codeplug.ChannelMode.NXDN);
            Assert.Equal(2, (int)Codeplug.ChannelMode.P25);
        }

        /// <summary>
        /// A system defaults its alias file to ./alias.yml relative to the
        /// working directory (Codeplug.cs line 125).
        /// </summary>
        [Fact]
        public void System_AliasPath_DefaultsToAliasYml()
        {
            var system = new Codeplug.System();

            Assert.Equal("./alias.yml", system.AliasPath);
        }

        /// <summary>
        /// A channel without an explicit mode resolves to P25 (the legacy
        /// default), exercising the portable Core-owned enum path.
        /// </summary>
        [Fact]
        public void Channel_GetChannelMode_DefaultsToP25()
        {
            var channel = new Codeplug.Channel();

            Assert.Equal(Codeplug.ChannelMode.P25, channel.GetChannelMode());
        }

        /// <summary>
        /// The default unencrypted algorithm id is the P25AlgoIds constant
        /// (0x80), proving the portable DvmConsole.Core constant resolves from
        /// Codeplug.GetAlgoId().
        /// </summary>
        [Fact]
        public void Channel_GetAlgoId_DefaultsToUnencrypt()
        {
            var channel = new Codeplug.Channel();

            Assert.Equal(P25AlgoIds.P25_ALGO_UNENCRYPT, channel.GetAlgoId());
            Assert.False(channel.HasEncryptionConfig());
            Assert.Equal(0, channel.GetKeyId());
        }

        /// <summary>
        /// A group defaults to the "patch" type.
        /// </summary>
        [Fact]
        public void Group_DefaultsToPatchType()
        {
            var group = new Codeplug.Group();

            Assert.Equal("patch", group.Type);
            Assert.True(group.IsPatchGroup());
            Assert.False(group.IsMultiselectGroup());
        }

        /*
        ** Group classification
        */

        /// <summary>
        /// Multiselect classification is case-insensitive and trims surrounding
        /// whitespace (Codeplug.cs Group.IsMultiselectGroup).
        /// </summary>
        [Fact]
        public void Group_MultiselectVariants_AreClassifiedAsMultiselect()
        {
            Assert.True(new Codeplug.Group { Type = "multiselect" }.IsMultiselectGroup());
            Assert.True(new Codeplug.Group { Type = "MULTISELECT" }.IsMultiselectGroup());
            Assert.True(new Codeplug.Group { Type = "MultiSelect" }.IsMultiselectGroup());
            Assert.True(new Codeplug.Group { Type = "  multiselect  " }.IsMultiselectGroup());
            Assert.True(new Codeplug.Group { Type = " multiselect" }.IsMultiselectGroup());
            Assert.False(new Codeplug.Group { Type = "multiselect" }.IsPatchGroup());
        }

        /// <summary>
        /// Anything other than an exact (case-insensitive, trimmed) "multiselect"
        /// is treated as a patch group: null, empty, whitespace, and unknown types.
        /// </summary>
        [Fact]
        public void Group_NullEmptyAndUnknownTypes_AreTreatedAsPatch()
        {
            Assert.True(new Codeplug.Group { Type = null }.IsPatchGroup());
            Assert.True(new Codeplug.Group { Type = "" }.IsPatchGroup());
            Assert.True(new Codeplug.Group { Type = "   " }.IsPatchGroup());
            Assert.True(new Codeplug.Group { Type = "PATCH" }.IsPatchGroup());
            Assert.True(new Codeplug.Group { Type = "dynamic" }.IsPatchGroup());
            Assert.False(new Codeplug.Group { Type = null }.IsMultiselectGroup());
            Assert.False(new Codeplug.Group { Type = "" }.IsMultiselectGroup());
            Assert.False(new Codeplug.Group { Type = "PATCH" }.IsMultiselectGroup());
        }

        /*
        ** NormalizeGroups
        */

        /// <summary>
        /// NormalizeGroups concatenates the current Groups and LegacyPatchGroups
        /// lists, in that order.
        /// </summary>
        [Fact]
        public void NormalizeGroups_MergesCurrentAndLegacyPatchGroups()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group>
                {
                    new Codeplug.Group { Name = "G1", Type = "patch" },
                    new Codeplug.Group { Name = "G2", Type = "multiselect" }
                },
                LegacyPatchGroups = new List<Codeplug.Group>
                {
                    new Codeplug.Group { Name = "L1" },
                    new Codeplug.Group { Name = "L2", Type = "patch" }
                }
            };

            codeplug.NormalizeGroups();

            Assert.Equal(new[] { "G1", "G2", "L1", "L2" }, codeplug.Groups.Select(g => g.Name));
        }

        /// <summary>
        /// Null entries from either source list are dropped.
        /// </summary>
        [Fact]
        public void NormalizeGroups_RemovesNullEntries()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group> { null, new Codeplug.Group { Name = "G1", Type = "patch" }, null },
                LegacyPatchGroups = new List<Codeplug.Group> { null, new Codeplug.Group { Name = "L1", Type = "patch" } }
            };

            codeplug.NormalizeGroups();

            Assert.Equal(new[] { "G1", "L1" }, codeplug.Groups.Select(g => g.Name));
        }

        /// <summary>
        /// Groups sharing a name after trimming, compared case-insensitively, are
        /// collapsed to a single entry; the first occurrence's type wins.
        /// </summary>
        [Fact]
        public void NormalizeGroups_DedupesByName_IgnoringCaseAndWhitespace()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group>
                {
                    new Codeplug.Group { Name = "Alpha", Type = "patch" },
                    new Codeplug.Group { Name = "  alpha  ", Type = "multiselect" },
                    new Codeplug.Group { Name = "ALPHA", Type = "patch" }
                }
            };

            codeplug.NormalizeGroups();

            var group = Assert.Single(codeplug.Groups);
            Assert.Equal("Alpha", group.Name);
            Assert.Equal("patch", group.Type);
        }

        /// <summary>
        /// Group names are trimmed of surrounding whitespace.
        /// </summary>
        [Fact]
        public void NormalizeGroups_TrimsNames()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group> { new Codeplug.Group { Name = "  Zone 1  ", Type = "patch" } }
            };

            codeplug.NormalizeGroups();

            Assert.Equal("Zone 1", Assert.Single(codeplug.Groups).Name);
        }

        /// <summary>
        /// Blank (null/empty/whitespace) types on current or legacy groups
        /// normalize to the lowercase "patch" type.
        /// </summary>
        [Fact]
        public void NormalizeGroups_BlankType_BecomesPatch()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group> { new Codeplug.Group { Name = "C1", Type = "  " } },
                LegacyPatchGroups = new List<Codeplug.Group>
                {
                    new Codeplug.Group { Name = "L1", Type = null },
                    new Codeplug.Group { Name = "L2", Type = "" }
                }
            };

            codeplug.NormalizeGroups();

            Assert.All(codeplug.Groups, g => Assert.Equal("patch", g.Type));
        }

        /// <summary>
        /// Non-blank types are trimmed and lowercased.
        /// </summary>
        [Fact]
        public void NormalizeGroups_LowercasesAndTrimsTypes()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group> { new Codeplug.Group { Name = "G1", Type = " MULTISELECT " } },
                LegacyPatchGroups = new List<Codeplug.Group> { new Codeplug.Group { Name = "L1", Type = "Patch" } }
            };

            codeplug.NormalizeGroups();

            Assert.Equal("multiselect", codeplug.Groups.Single(g => g.Name == "G1").Type);
            Assert.Equal("patch", codeplug.Groups.Single(g => g.Name == "L1").Type);
        }

        /// <summary>
        /// On a name collision between current and legacy lists, the current
        /// (first) entry wins because Groups precedes LegacyPatchGroups.
        /// </summary>
        [Fact]
        public void NormalizeGroups_CurrentGroupWinsOverLegacyOnNameCollision()
        {
            var codeplug = new Codeplug
            {
                Groups = new List<Codeplug.Group> { new Codeplug.Group { Name = "Shared", Type = "multiselect" } },
                LegacyPatchGroups = new List<Codeplug.Group> { new Codeplug.Group { Name = " shared ", Type = "" } }
            };

            codeplug.NormalizeGroups();

            var group = Assert.Single(codeplug.Groups);
            Assert.Equal("Shared", group.Name);
            Assert.Equal("multiselect", group.Type);
        }

        /// <summary>
        /// A codeplug with null Groups and null LegacyPatchGroups still ends up
        /// with a non-null, empty Groups list.
        /// </summary>
        [Fact]
        public void NormalizeGroups_NullLists_YieldNonNullEmptyGroups()
        {
            var codeplug = new Codeplug { Groups = null, LegacyPatchGroups = null };

            codeplug.NormalizeGroups();

            Assert.NotNull(codeplug.Groups);
            Assert.Empty(codeplug.Groups);
        }

        /*
        ** Channel encryption mapping
        */

        /// <summary>
        /// Algo strings map (case-insensitively) to the DvmConsole.Core
        /// P25AlgoIds algorithm id constants.
        /// </summary>
        [Fact]
        public void Channel_GetAlgoId_MapsKnownAlgorithmsToP25AlgoIdsConstants()
        {
            Assert.Equal(P25AlgoIds.P25_ALGO_AES, new Codeplug.Channel { Algo = "aes" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_AES, new Codeplug.Channel { Algo = "AES" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_DES, new Codeplug.Channel { Algo = "des" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_DES, new Codeplug.Channel { Algo = "DES" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_ARC4, new Codeplug.Channel { Algo = "arc4" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_ARC4, new Codeplug.Channel { Algo = "Arc4" }.GetAlgoId());
        }

        /// <summary>
        /// Unknown, null, and default ("none") algo strings resolve to the
        /// unencrypt constant rather than throwing.
        /// </summary>
        [Fact]
        public void Channel_GetAlgoId_UnknownOrNullAlgorithm_ReturnsUnencrypt()
        {
            Assert.Equal(P25AlgoIds.P25_ALGO_UNENCRYPT, new Codeplug.Channel { Algo = "adp" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_UNENCRYPT, new Codeplug.Channel { Algo = "none" }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_UNENCRYPT, new Codeplug.Channel { Algo = null }.GetAlgoId());
            Assert.Equal(P25AlgoIds.P25_ALGO_UNENCRYPT, new Codeplug.Channel().GetAlgoId());
        }

        /// <summary>
        /// KeyId is parsed as base-16 ("A1" = 161); blank/null maps to 0.
        /// </summary>
        [Fact]
        public void Channel_GetKeyId_ParsesPlainHexAndDefaultsToZero()
        {
            Assert.Equal(0xA1, new Codeplug.Channel { KeyId = "A1" }.GetKeyId());
            Assert.Equal(0xA1, new Codeplug.Channel { KeyId = "a1" }.GetKeyId());
            Assert.Equal(0, new Codeplug.Channel { KeyId = null }.GetKeyId());
            Assert.Equal(0, new Codeplug.Channel { KeyId = "" }.GetKeyId());
            Assert.Equal(0, new Codeplug.Channel { KeyId = "   " }.GetKeyId());
            Assert.Equal(0, new Codeplug.Channel().GetKeyId());
        }

        /// <summary>
        /// Encryption is configured only when the algorithm is encrypted and the
        /// key id is greater than zero.
        /// </summary>
        [Fact]
        public void Channel_HasEncryptionConfig_RequiresEncryptedAlgoAndPositiveKey()
        {
            Assert.True(new Codeplug.Channel { Algo = "aes", KeyId = "A1" }.HasEncryptionConfig());
            Assert.True(new Codeplug.Channel { Algo = "DES", KeyId = "01" }.HasEncryptionConfig());
            Assert.True(new Codeplug.Channel { Algo = "arc4", KeyId = "ff" }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = "aes", KeyId = null }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = "aes", KeyId = "" }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = "aes", KeyId = "0" }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = "none", KeyId = "A1" }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = null, KeyId = "A1" }.HasEncryptionConfig());
            Assert.False(new Codeplug.Channel { Algo = "unknown", KeyId = "A1" }.HasEncryptionConfig());
        }

        /*
        ** Channel mode parsing
        */

        /// <summary>
        /// Mode parsing is case-insensitive for all three modes.
        /// </summary>
        [Fact]
        public void Channel_GetChannelMode_ParsesCaseInsensitively()
        {
            Assert.Equal(Codeplug.ChannelMode.DMR, new Codeplug.Channel { Mode = "dmr" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.DMR, new Codeplug.Channel { Mode = "DMR" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.NXDN, new Codeplug.Channel { Mode = "nxdn" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.NXDN, new Codeplug.Channel { Mode = "Nxdn" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel { Mode = "p25" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel { Mode = "P25" }.GetChannelMode());
        }

        /// <summary>
        /// Unparseable or blank modes fall back to P25 (the legacy default).
        /// Note: Enum.TryParse trims whitespace, so "dmr " parses as DMR and is
        /// not an invalid mode.
        /// </summary>
        [Fact]
        public void Channel_GetChannelMode_InvalidModeDefaultsToP25()
        {
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel { Mode = "analog" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel { Mode = "" }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel { Mode = null }.GetChannelMode());
            Assert.Equal(Codeplug.ChannelMode.P25, new Codeplug.Channel().GetChannelMode());
        }

        /*
        ** Lookups
        */

        /// <summary>
        /// The Channel overload resolves the channel's System property against
        /// the Systems list with an exact (case-sensitive) name match.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_ChannelOverload_ReturnsExactNameSystem()
        {
            var alpha = new Codeplug.System { Name = "Alpha" };
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { alpha, new Codeplug.System { Name = "Beta" } }
            };

            Assert.Same(alpha, codeplug.GetSystemForChannel(new Codeplug.Channel { System = "Alpha" }));
        }

        /// <summary>
        /// A channel naming a system that is not configured resolves to null.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_ChannelOverload_MissingSystem_ReturnsNull()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Alpha" } }
            };

            Assert.Null(codeplug.GetSystemForChannel(new Codeplug.Channel { System = "NotThere" }));
        }

        /// <summary>
        /// The name comparison is case-sensitive: "alpha" does not match "Alpha".
        /// </summary>
        [Fact]
        public void GetSystemForChannel_ChannelOverload_IsCaseSensitive()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Alpha" } }
            };

            Assert.Null(codeplug.GetSystemForChannel(new Codeplug.Channel { System = "alpha" }));
        }

        /// <summary>
        /// The string overload scans every zone (in order) for the channel name
        /// and resolves the matched channel's system.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_StringOverload_SearchesAllZones()
        {
            var sys2 = new Codeplug.System { Name = "Sys2" };
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Sys1" }, sys2 },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone { Name = "Zone1", Channels = new List<Codeplug.Channel> { new Codeplug.Channel { Name = "Ch1", System = "Sys1" } } },
                    new Codeplug.Zone { Name = "Zone2", Channels = new List<Codeplug.Channel> { new Codeplug.Channel { Name = "Ch2", System = "Sys2" } } }
                }
            };

            Assert.Same(sys2, codeplug.GetSystemForChannel("Ch2"));
        }

        /// <summary>
        /// A channel name that appears in no zone resolves to null.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_StringOverload_MissingChannel_ReturnsNull()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Sys1" } },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone { Name = "Zone1", Channels = new List<Codeplug.Channel> { new Codeplug.Channel { Name = "Ch1", System = "Sys1" } } }
                }
            };

            Assert.Null(codeplug.GetSystemForChannel("NotThere"));
        }

        /// <summary>
        /// With null Zones the string overload returns null without throwing.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_StringOverload_NullZones_ReturnsNull()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Sys1" } },
                Zones = null
            };

            Assert.Null(codeplug.GetSystemForChannel("Ch1"));
        }

        /// <summary>
        /// A matched channel whose system name is not configured resolves to null.
        /// </summary>
        [Fact]
        public void GetSystemForChannel_StringOverload_UnconfiguredSystem_ReturnsNull()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System> { new Codeplug.System { Name = "Sys1" } },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone { Name = "Zone1", Channels = new List<Codeplug.Channel> { new Codeplug.Channel { Name = "Ch1", System = "Ghost" } } }
                }
            };

            Assert.Null(codeplug.GetSystemForChannel("Ch1"));
        }

        /// <summary>
        /// GetChannelByName returns the first channel whose name matches, scanning
        /// zones in order.
        /// </summary>
        [Fact]
        public void GetChannelByName_ReturnsFirstMatchingChannelAcrossZones()
        {
            var zone1Channel = new Codeplug.Channel { Name = "Dup", System = "Sys1" };
            var zone2Channel = new Codeplug.Channel { Name = "Dup", System = "Sys2" };
            var codeplug = new Codeplug
            {
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone { Name = "Zone1", Channels = new List<Codeplug.Channel> { zone1Channel } },
                    new Codeplug.Zone { Name = "Zone2", Channels = new List<Codeplug.Channel> { zone2Channel } }
                }
            };

            Assert.Same(zone1Channel, codeplug.GetChannelByName("Dup"));
        }

        /// <summary>
        /// A channel name that appears in no zone resolves to null.
        /// </summary>
        [Fact]
        public void GetChannelByName_MissingChannel_ReturnsNull()
        {
            var codeplug = new Codeplug
            {
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone { Name = "Zone1", Channels = new List<Codeplug.Channel> { new Codeplug.Channel { Name = "Ch1" } } }
                }
            };

            Assert.Null(codeplug.GetChannelByName("NotThere"));
        }

        /// <summary>
        /// With null Zones, GetChannelByName returns null without throwing.
        /// </summary>
        [Fact]
        public void GetChannelByName_NullZones_ReturnsNull()
        {
            var codeplug = new Codeplug { Zones = null };

            Assert.Null(codeplug.GetChannelByName("Ch1"));
        }

        /*
        ** Contract defaults
        */

        /// <summary>
        /// A channel defaults to the "normal" card size.
        /// </summary>
        [Fact]
        public void Channel_CardSize_DefaultsToNormal()
        {
            Assert.Equal("normal", new Codeplug.Channel().CardSize);
        }

        /// <summary>
        /// System.ToString returns the system name.
        /// </summary>
        [Fact]
        public void System_ToString_ReturnsName()
        {
            var system = new Codeplug.System { Name = "Alpha" };

            Assert.Equal("Alpha", system.ToString());
        }
    }
}
