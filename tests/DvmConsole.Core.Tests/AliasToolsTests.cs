// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for Core AliasTools (WPF-shared, currently ZERO
* test coverage). Locks the existing WPF contract so the Avalonia
* alias slice (deleg_79328deb follow-on) can build on it:
*
*   AliasTools.LoadAliases(filePath): List<RadioAlias> — throws
*     FileNotFoundException when the file is missing; parses the
*     YamlDotNet list (RadioAlias { Alias, Rid }) with the CamelCase
*     naming convention.
*   AliasTools.GetAliasByRid(aliases, rid): string — the first alias
*     whose Rid matches, or string.Empty when null/empty/no-match.
*/
using System;
using System.Collections.Generic;
using System.IO;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="AliasTools"/>.
    /// </summary>
    public sealed class AliasToolsTests
    {
        /* ------------------------------------------------------------------
        ** GetAliasByRid
        ** ---------------------------------------------------------------- */

        [Fact]
        public void GetAliasByRid_Match_ReturnsAlias()
        {
            var aliases = new List<RadioAlias>
            {
                new RadioAlias { Rid = 1001, Alias = "Alpha" },
                new RadioAlias { Rid = 1002, Alias = "Bravo" },
            };

            Assert.Equal("Alpha", AliasTools.GetAliasByRid(aliases, 1001));
            Assert.Equal("Bravo", AliasTools.GetAliasByRid(aliases, 1002));
        }

        [Fact]
        public void GetAliasByRid_FirstMatchWins()
        {
            var aliases = new List<RadioAlias>
            {
                new RadioAlias { Rid = 1001, Alias = "First" },
                new RadioAlias { Rid = 1001, Alias = "Second" },
            };

            Assert.Equal("First", AliasTools.GetAliasByRid(aliases, 1001));
        }

        [Fact]
        public void GetAliasByRid_NoMatch_Empty()
        {
            var aliases = new List<RadioAlias>
            {
                new RadioAlias { Rid = 1001, Alias = "Alpha" },
            };

            Assert.Equal(string.Empty, AliasTools.GetAliasByRid(aliases, 9999));
        }

        [Fact]
        public void GetAliasByRid_NullOrEmptyList_Empty_NeverThrows()
        {
            Assert.Equal(string.Empty, AliasTools.GetAliasByRid(null!, 1001));
            Assert.Equal(string.Empty, AliasTools.GetAliasByRid(new List<RadioAlias>(), 1001));
        }

        /* ------------------------------------------------------------------
        ** LoadAliases
        ** ---------------------------------------------------------------- */

        [Fact]
        public void LoadAliases_MissingFile_ThrowsFileNotFound()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");

            Assert.Throws<FileNotFoundException>(() => AliasTools.LoadAliases(missing));
        }

        [Fact]
        public void LoadAliases_ValidYaml_ParsesList()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                File.WriteAllText(path,
                    "- alias: Alpha Base\n  rid: 1001\n" +
                    "- alias: Bravo Repeater\n  rid: 1002\n");

                var aliases = AliasTools.LoadAliases(path);

                Assert.Equal(2, aliases.Count);
                Assert.Equal(1001, aliases[0].Rid);
                Assert.Equal("Alpha Base", aliases[0].Alias);
                Assert.Equal(1002, aliases[1].Rid);
                Assert.Equal("Bravo Repeater", aliases[1].Alias);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
