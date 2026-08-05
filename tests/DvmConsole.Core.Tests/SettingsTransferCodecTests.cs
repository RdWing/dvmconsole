// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic contract tests for the portable settings-transfer codec
* (DvmConsole.Core/Configuration/SettingsTransferFile.cs,
* SettingsTransferCategoryDefinition.cs, SettingsTransferCodec.cs). These
* lock the transfer file DTO shape, the golden byte-exact serialization
* format, the ReadFile/WriteFile exception contract, the token conversion
* matrix, and the category/payload resolution rules that SettingsManager
* delegates to. Every test is headless and POCO-only: no WPF types, no
* fnecore, no SettingsManager.
*/
using dvmconsole;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Contract tests for the portable settings-transfer codec and its two
    /// DTO types.
    /// </summary>
    public class SettingsTransferCodecTests
    {
        /// <summary>
        /// Hermetic per-test temp directory; deleted on dispose.
        /// </summary>
        private sealed class TempDir : IDisposable
        {
            public string Root { get; }

            public TempDir()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-transfer-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Root, true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        /// <summary>
        /// Byte-exact serialized form of the golden transfer file. Locks
        /// PascalCase property names in declaration order, Newtonsoft
        /// 2-space indentation, ISO-8601 UTC date, integer enums, and the
        /// absence of TypeNameHandling metadata.
        /// </summary>
        private const string GOLDEN_JSON = """
{
  "Format": "dvmconsole-settings-transfer",
  "Version": 1,
  "ExportedUtc": "2026-08-04T12:34:56.789Z",
  "Categories": [
    "layout",
    "audio"
  ],
  "Settings": {
    "Name": "Console A",
    "Count": 42,
    "Ratio": 0.5,
    "Enabled": true,
    "NullValue": null,
    "Day": 3,
    "Items": [
      1,
      2,
      3
    ],
    "Lookup": {
      "alpha": "a",
      "beta": "b"
    },
    "Mapping": {
      "one": 1,
      "two": 2
    }
  }
}
""";

        /// <summary>
        /// Sample category definitions mirroring the real definition order.
        /// </summary>
        private static List<SettingsTransferCategoryDefinition> SampleDefinitions()
        {
            return new List<SettingsTransferCategoryDefinition>
            {
                new SettingsTransferCategoryDefinition { Id = "layout", DisplayName = "Console Layout" },
                new SettingsTransferCategoryDefinition { Id = "audio", DisplayName = "Audio Routing" },
                new SettingsTransferCategoryDefinition { Id = "tar", DisplayName = "Talkgroup Audio Recorder" },
                new SettingsTransferCategoryDefinition { Id = "keys-security", DisplayName = "Keybinds and Selectable Encryption" }
            };
        }

        /*
        ** Ownership
        */

        /// <summary>
        /// All three transfer types must compile into the portable
        /// DvmConsole.Core assembly (not the WPF app), and the Core assembly
        /// must not reference any WPF/desktop framework.
        /// </summary>
        [Fact]
        public void TransferTypes_LiveInDvmConsoleCoreAssembly()
        {
            Assert.Equal("DvmConsole.Core", typeof(SettingsTransferFile).Assembly.GetName().Name);
            Assert.Equal("DvmConsole.Core", typeof(SettingsTransferCategoryDefinition).Assembly.GetName().Name);
            Assert.Equal("DvmConsole.Core", typeof(SettingsTransferCodec).Assembly.GetName().Name);
            Assert.Same(typeof(SettingsTransferFile).Assembly, typeof(SettingsTransferCodec).Assembly);

            string[] referenced = typeof(SettingsTransferCodec).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToArray();
            Assert.DoesNotContain("WindowsBase", referenced);
            Assert.DoesNotContain("PresentationFramework", referenced);
            Assert.DoesNotContain("PresentationCore", referenced);
            Assert.DoesNotContain("System.Windows.Forms", referenced);
            Assert.DoesNotContain("Avalonia", referenced);
        }

        /*
        ** DTO default shapes
        */

        /// <summary>
        /// Default DTO shapes: transfer file header defaults (format
        /// constant, Version 1, now-UTC export stamp, empty categories and
        /// settings) and category definition defaults.
        /// </summary>
        [Fact]
        public void Dto_DefaultShapes()
        {
            SettingsTransferFile file = new SettingsTransferFile();
            Assert.Equal("dvmconsole-settings-transfer", file.Format);
            Assert.Equal(1, file.Version);
            Assert.Equal(DateTimeKind.Utc, file.ExportedUtc.Kind);
            Assert.True((DateTime.UtcNow - file.ExportedUtc).Duration() < TimeSpan.FromMinutes(1));
            Assert.NotNull(file.Categories);
            Assert.Empty(file.Categories);
            Assert.NotNull(file.Settings);
            Assert.Empty(file.Settings);

            SettingsTransferCategoryDefinition category = new SettingsTransferCategoryDefinition();
            Assert.Equal(string.Empty, category.Id);
            Assert.Equal(string.Empty, category.DisplayName);
            Assert.Equal(string.Empty, category.Description);
            Assert.NotNull(category.PropertyNames);
            Assert.Empty(category.PropertyNames);
        }

        /*
        ** Serialization
        */

        /// <summary>
        /// Golden byte-exact serialization: fixed UTC export stamp,
        /// representative nested JObject/JArray/dict/null/int/double/bool/
        /// string/enum payload, asserting PascalCase, declaration order,
        /// 2-space indentation, ISO date, integer enum, and no
        /// TypeNameHandling metadata.
        /// </summary>
        [Fact]
        public void Serialize_GoldenByteExact()
        {
            SettingsTransferFile file = new SettingsTransferFile
            {
                ExportedUtc = new DateTime(2026, 8, 4, 12, 34, 56, 789, DateTimeKind.Utc),
                Categories = new List<string> { "layout", "audio" },
                Settings = new JObject
                {
                    ["Name"] = "Console A",
                    ["Count"] = 42,
                    ["Ratio"] = 0.5,
                    ["Enabled"] = true,
                    ["NullValue"] = JValue.CreateNull(),
                    ["Day"] = JToken.FromObject(DayOfWeek.Wednesday),
                    ["Items"] = new JArray(1, 2, 3),
                    ["Lookup"] = new JObject { ["alpha"] = "a", ["beta"] = "b" },
                    ["Mapping"] = new JObject { ["one"] = 1, ["two"] = 2 }
                }
            };

            string json = SettingsTransferCodec.Serialize(file);

            Assert.Equal(GOLDEN_JSON, json);
            Assert.DoesNotContain("$type", json);
        }

        /// <summary>
        /// Deserialize(Serialize(...)) and WriteFile/ReadFile round trips
        /// must reproduce the DTO exactly, including nested payload deep
        /// equality.
        /// </summary>
        [Fact]
        public void DeserializeAndReadFile_RoundTripDeepEquality()
        {
            SettingsTransferFile file = new SettingsTransferFile
            {
                ExportedUtc = new DateTime(2026, 8, 4, 12, 34, 56, 789, DateTimeKind.Utc),
                Categories = new List<string> { "layout", "audio" },
                Settings = new JObject
                {
                    ["Name"] = "Console A",
                    ["Count"] = 42,
                    ["Items"] = new JArray(1, 2, 3),
                    ["Lookup"] = new JObject { ["alpha"] = "a" }
                }
            };

            SettingsTransferFile viaJson = SettingsTransferCodec.Deserialize(SettingsTransferCodec.Serialize(file));
            Assert.Equal(file.Format, viaJson.Format);
            Assert.Equal(file.Version, viaJson.Version);
            Assert.Equal(file.ExportedUtc, viaJson.ExportedUtc);
            Assert.Equal(file.Categories, viaJson.Categories);
            Assert.True(JToken.DeepEquals(file.Settings, viaJson.Settings));

            using (TempDir temp = new TempDir())
            {
                string path = Path.Combine(temp.Root, "roundtrip.json");
                SettingsTransferCodec.WriteFile(file, path);
                SettingsTransferFile viaFile = SettingsTransferCodec.ReadFile(path);
                Assert.Equal(file.ExportedUtc, viaFile.ExportedUtc);
                Assert.Equal(file.Categories, viaFile.Categories);
                Assert.True(JToken.DeepEquals(file.Settings, viaFile.Settings));
            }
        }

        /*
        ** ReadFile exception contract
        */

        /// <summary>
        /// Blank paths are rejected with the exact argument exception used
        /// by the WPF import path guard.
        /// </summary>
        [Fact]
        public void ReadFile_BlankPath_ThrowsArgumentException()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => SettingsTransferCodec.ReadFile("   "));
            Assert.StartsWith("Import path is required.", ex.Message);
            Assert.Equal("filePath", ex.ParamName);
        }

        /// <summary>
        /// Missing files surface the exact file-not-found contract with the
        /// offending path attached.
        /// </summary>
        [Fact]
        public void ReadFile_MissingPath_ThrowsFileNotFoundException()
        {
            string missing = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-transfer-" + Guid.NewGuid().ToString("N") + ".json");
            FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => SettingsTransferCodec.ReadFile(missing));
            Assert.Equal("Settings transfer file was not found.", ex.Message);
            Assert.Equal(missing, ex.FileName);
        }

        /// <summary>
        /// A null DTO (file content "null") or a DTO with null Settings is
        /// rejected as not a valid transfer file.
        /// </summary>
        [Fact]
        public void ReadFile_NullDtoOrNullSettings_ThrowsInvalidOperationException()
        {
            using (TempDir temp = new TempDir())
            {
                string nullDto = Path.Combine(temp.Root, "null.json");
                File.WriteAllText(nullDto, "null");
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SettingsTransferCodec.ReadFile(nullDto));
                Assert.Equal("The selected file is not a valid settings transfer file.", ex.Message);

                string nullSettings = Path.Combine(temp.Root, "nullsettings.json");
                File.WriteAllText(nullSettings, "{\"Format\":\"dvmconsole-settings-transfer\",\"Settings\":null}");
                ex = Assert.Throws<InvalidOperationException>(() => SettingsTransferCodec.ReadFile(nullSettings));
                Assert.Equal("The selected file is not a valid settings transfer file.", ex.Message);
            }
        }

        /// <summary>
        /// A mismatched Format is rejected case-insensitively against the
        /// exact format identifier, and the same identifier in different
        /// case is accepted (OrdinalIgnoreCase).
        /// </summary>
        [Fact]
        public void ReadFile_BadFormat_Throws_AndCaseInsensitiveFormatAccepted()
        {
            using (TempDir temp = new TempDir())
            {
                string bad = Path.Combine(temp.Root, "badformat.json");
                File.WriteAllText(bad, "{\"Format\":\"other-console-transfer\",\"Settings\":{}}");
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SettingsTransferCodec.ReadFile(bad));
                Assert.Equal("The selected file is not a dvmconsole settings transfer file.", ex.Message);

                string upper = Path.Combine(temp.Root, "uppercaseformat.json");
                File.WriteAllText(upper, "{\"Format\":\"DVMConsole-Settings-Transfer\",\"Settings\":{}}");
                SettingsTransferFile accepted = SettingsTransferCodec.ReadFile(upper);
                Assert.Equal("DVMConsole-Settings-Transfer", accepted.Format);
            }
        }

        /// <summary>
        /// Version is written but never validated: an out-of-range version
        /// must be accepted and preserved (current behavior quirk).
        /// </summary>
        [Fact]
        public void ReadFile_VersionMismatch_Accepted()
        {
            using (TempDir temp = new TempDir())
            {
                string path = Path.Combine(temp.Root, "futureversion.json");
                File.WriteAllText(path, "{\"Format\":\"dvmconsole-settings-transfer\",\"Version\":999,\"Settings\":{}}");
                SettingsTransferFile file = SettingsTransferCodec.ReadFile(path);
                Assert.Equal(999, file.Version);
            }
        }

        /*
        ** ConvertToken matrix
        */

        /// <summary>
        /// Null tokens: null for reference types and nullable value types,
        /// Activator default instance for non-nullable value types.
        /// </summary>
        [Fact]
        public void ConvertToken_NullTokens_ReturnNullOrDefaults()
        {
            Assert.Null(SettingsTransferCodec.ConvertToken(null, typeof(string)));
            Assert.Null(SettingsTransferCodec.ConvertToken(JValue.CreateNull(), typeof(string)));
            Assert.Null(SettingsTransferCodec.ConvertToken(JValue.CreateNull(), typeof(int?)));
            Assert.Equal(0, SettingsTransferCodec.ConvertToken(JValue.CreateNull(), typeof(int)));
        }

        /// <summary>
        /// Primitive conversions round-trip through JToken.FromObject.
        /// </summary>
        public static IEnumerable<object[]> PrimitiveCases()
        {
            yield return new object[] { "abc", typeof(string), "abc" };
            yield return new object[] { 42, typeof(int), 42 };
            yield return new object[] { 3.5, typeof(double), 3.5 };
            yield return new object[] { true, typeof(bool), true };
        }

        [Theory]
        [MemberData(nameof(PrimitiveCases))]
        public void ConvertToken_PrimitiveValues(object input, Type targetType, object expected)
        {
            object result = SettingsTransferCodec.ConvertToken(JToken.FromObject(input), targetType);
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// Enum conversions accept both the underlying integer and the enum
        /// name (default Newtonsoft behavior, no StringEnumConverter).
        /// </summary>
        public static IEnumerable<object[]> EnumCases()
        {
            yield return new object[] { 3, typeof(DayOfWeek), DayOfWeek.Wednesday };
            yield return new object[] { "Wednesday", typeof(DayOfWeek), DayOfWeek.Wednesday };
        }

        [Theory]
        [MemberData(nameof(EnumCases))]
        public void ConvertToken_EnumValues(object input, Type targetType, object expected)
        {
            object result = SettingsTransferCodec.ConvertToken(JToken.FromObject(input), targetType);
            Assert.Equal(expected, result);
        }

        /// <summary>
        /// List and dictionary targets convert from JArray/JObject tokens.
        /// </summary>
        [Fact]
        public void ConvertToken_Collections()
        {
            List<int> list = (List<int>)SettingsTransferCodec.ConvertToken(new JArray(1, 2, 3), typeof(List<int>));
            Assert.Equal(new List<int> { 1, 2, 3 }, list);

            Dictionary<string, int> dict = (Dictionary<string, int>)SettingsTransferCodec.ConvertToken(
                new JObject { ["alpha"] = 1, ["beta"] = 2 },
                typeof(Dictionary<string, int>));
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["alpha"]);
            Assert.Equal(2, dict["beta"]);
        }

        /*
        ** ResolveCategories
        */

        /// <summary>
        /// Category selection: empty/blank/unknown ids yield nothing;
        /// matching is case-insensitive with trimmed ids; results always
        /// follow definition order regardless of input order.
        /// </summary>
        public static IEnumerable<object[]> ResolveCases()
        {
            yield return new object[] { new string[0], new string[0] };
            yield return new object[] { new[] { "  ", "", null }, new string[0] };
            yield return new object[] { new[] { "nope", "tar" }, new[] { "tar" } };
            yield return new object[] { new[] { " LAYOUT ", "Audio" }, new[] { "layout", "audio" } };
            yield return new object[] { new[] { "keys-security", "audio" }, new[] { "audio", "keys-security" } };
        }

        [Theory]
        [MemberData(nameof(ResolveCases))]
        public void ResolveCategories_SelectionRules(string[] categoryIds, string[] expectedIds)
        {
            List<SettingsTransferCategoryDefinition> resolved = SettingsTransferCodec.ResolveCategories(SampleDefinitions(), categoryIds);
            Assert.Equal(expectedIds, resolved.Select(c => c.Id).ToArray());
        }

        /*
        ** BuildPayload
        */

        /// <summary>
        /// Payload source with a readable null property, a write-only
        /// property, and a public settable property.
        /// </summary>
        private sealed class PayloadSource
        {
            public string Name { get; set; } = "Alpha";
            public int Count { get; set; } = 42;
            public string Nullable { get; set; } = null;
            public string WriteOnly { set { } }
        }

        /// <summary>
        /// Property names are case-insensitively distinct in first-occurrence
        /// order, null values are preserved as null tokens, and missing or
        /// unreadable property names are skipped.
        /// </summary>
        [Fact]
        public void BuildPayload_DistinctOrderNullsAndSkips()
        {
            JObject payload = SettingsTransferCodec.BuildPayload(
                new PayloadSource(),
                new[] { "Count", "count", "Name", "Nullable", "Nope", "WriteOnly" });

            Assert.Equal(3, payload.Count);
            Assert.Equal(new[] { "Count", "Name", "Nullable" }, payload.Properties().Select(p => p.Name).ToArray());
            Assert.Equal(42, (int)payload["Count"]);
            Assert.Equal("Alpha", (string)payload["Name"]);
            Assert.Equal(JTokenType.Null, payload["Nullable"].Type);
        }

        /// <summary>
        /// Source with a getter that counts invocations.
        /// </summary>
        private sealed class CountingSource
        {
            public int Reads { get; private set; }

            public string Name
            {
                get { Reads++; return "x"; }
            }
        }

        /// <summary>
        /// The getter must be invoked exactly once per distinct property
        /// name even when duplicates differ in case.
        /// </summary>
        [Fact]
        public void BuildPayload_GetterCalledOncePerDistinctName()
        {
            CountingSource source = new CountingSource();
            JObject payload = SettingsTransferCodec.BuildPayload(source, new[] { "Name", "name", "NAME" });

            Assert.Equal(1, source.Reads);
            Assert.Equal("x", (string)payload["Name"]);
        }

        /*
        ** WriteFile
        */

        /// <summary>
        /// WriteFile creates the full nested parent directory chain and the
        /// written file round-trips through ReadFile.
        /// </summary>
        [Fact]
        public void WriteFile_CreatesNestedParentDirectories()
        {
            using (TempDir temp = new TempDir())
            {
                string path = Path.Combine(temp.Root, "nested", "deep", "export.json");
                SettingsTransferFile file = new SettingsTransferFile
                {
                    ExportedUtc = new DateTime(2026, 8, 4, 12, 34, 56, 789, DateTimeKind.Utc),
                    Categories = new List<string> { "layout" },
                    Settings = new JObject { ["Name"] = "Alpha" }
                };

                SettingsTransferCodec.WriteFile(file, path);

                Assert.True(File.Exists(path));
                SettingsTransferFile roundTripped = SettingsTransferCodec.ReadFile(path);
                Assert.Equal(file.ExportedUtc, roundTripped.ExportedUtc);
                Assert.Equal(file.Categories, roundTripped.Categories);
                Assert.True(JToken.DeepEquals(file.Settings, roundTripped.Settings));
            }
        }
    }
}
