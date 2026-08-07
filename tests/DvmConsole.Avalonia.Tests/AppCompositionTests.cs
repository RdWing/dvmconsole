// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using System.IO;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class AppCompositionTests
    {
        [Fact]
        public void BuildAliasResolver_UsesInlineAliasesAndSkipsMalformedSystems()
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System>
                {
                    null!,
                    new Codeplug.System
                    {
                        Name = "Inline System",
                        AliasPath = Path.Combine(Path.GetTempPath(), "missing-alias-" + System.Guid.NewGuid().ToString("N") + ".yml"),
                        RidAlias = new List<RadioAlias>
                        {
                            new RadioAlias { Rid = 1001, Alias = "Inline Alias" },
                        },
                    },
                    new Codeplug.System { Name = "" },
                },
            };

            var resolver = App.BuildAliasResolver(codeplug);

            Assert.NotNull(resolver);
            Assert.Equal("Inline Alias", resolver!.Resolve("inline system", 1001));
        }

        [Fact]
        public void BuildAliasResolver_ExternalFileOverridesInlineAliases()
        {
            var path = Path.Combine(Path.GetTempPath(), "aliases-" + System.Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                File.WriteAllText(path, "- alias: External Alias\n  rid: 1001\n");
                var resolver = App.BuildAliasResolver(new Codeplug
                {
                    Systems = new List<Codeplug.System>
                    {
                        new Codeplug.System
                        {
                            Name = "System 1",
                            AliasPath = path,
                            RidAlias = new List<RadioAlias>
                            {
                                new RadioAlias { Rid = 1001, Alias = "Inline Alias" },
                            },
                        },
                    },
                });

                Assert.Equal("External Alias", resolver!.Resolve("System 1", 1001));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CreateCallHistoryStore_SuppressesConfiguredConsoleRid()
        {
            var store = App.CreateCallHistoryStore(new Codeplug
            {
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "System 1", Rid = "1001" },
                },
            });

            store.AddFrame(
                new ReceivedCallMetadata("System 1", 1001, 2000, 0, VoiceMode.P25, 1, "System 1|2000", false),
                "CH 1");
            store.AddFrame(
                new ReceivedCallMetadata("System 1", 2002, 2000, 0, VoiceMode.P25, 2, "System 1|2000", false),
                "CH 1");

            var entry = Assert.Single(store.Entries);
            Assert.Equal(2002u, entry.SrcId);
        }
    }
}
