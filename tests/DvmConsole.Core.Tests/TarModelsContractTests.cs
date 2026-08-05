// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/
/**
* Deterministic JSON/API contract tests for the TAR recording models
* (DvmConsole.Core/Configuration/TarModels.cs): the TarRecordingDirection and
* TarRecordingSourceType enums, TarChannelConfig, and TarRecordingMetadata.
* These lock the serialization-facing surface (enum names/values, constructor
* defaults, PascalCase JSON key names and declaration order, round-trip and
* deserialization semantics) that the WPF TAR recorder and the recording
* metadata sidecar files depend on. All assertions are deterministic: no
* wall-clock time, no exact GUID values, no external files.
*/
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.Reflection;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="TarRecordingDirection"/>,
    /// <see cref="TarRecordingSourceType"/>, <see cref="TarChannelConfig"/>
    /// and <see cref="TarRecordingMetadata"/>.
    /// </summary>
    public class TarModelsContractTests
    {
        /*
        ** Enum contracts
        */

        /// <summary>
        /// The TarRecordingDirection enum values are part of the on-disk
        /// recording metadata schema: RX=0, TX=1. They must never be renumbered.
        /// </summary>
        [Fact]
        public void TarRecordingDirection_EnumValues_AreStableContract()
        {
            Assert.Equal(0, (int)TarRecordingDirection.RX);
            Assert.Equal(1, (int)TarRecordingDirection.TX);

            Assert.Equal(new[] { "RX", "TX" }, Enum.GetNames(typeof(TarRecordingDirection)));
        }

        /// <summary>
        /// The TarRecordingSourceType enum values are part of the on-disk
        /// recording metadata schema: InboundRadio=0, ConsoleTx=1. They must
        /// never be renumbered.
        /// </summary>
        [Fact]
        public void TarRecordingSourceType_EnumValues_AreStableContract()
        {
            Assert.Equal(0, (int)TarRecordingSourceType.InboundRadio);
            Assert.Equal(1, (int)TarRecordingSourceType.ConsoleTx);

            Assert.Equal(new[] { "InboundRadio", "ConsoleTx" }, Enum.GetNames(typeof(TarRecordingSourceType)));
        }

        /// <summary>
        /// Both enums carry the StringEnumConverter attribute, so JSON uses
        /// the enum member names ("RX", "TX", "InboundRadio", "ConsoleTx"),
        /// never numeric values.
        /// </summary>
        [Fact]
        public void TarEnums_CarryStringEnumConverterAttribute()
        {
            var directionAttr = typeof(TarRecordingDirection).GetCustomAttribute<JsonConverterAttribute>();
            var sourceTypeAttr = typeof(TarRecordingSourceType).GetCustomAttribute<JsonConverterAttribute>();

            Assert.NotNull(directionAttr);
            Assert.NotNull(sourceTypeAttr);
            Assert.Equal(typeof(StringEnumConverter), directionAttr.ConverterType);
            Assert.Equal(typeof(StringEnumConverter), sourceTypeAttr.ConverterType);
        }

        /// <summary>
        /// Serializing the enums directly yields their member names, not
        /// numeric ordinals.
        /// </summary>
        [Fact]
        public void TarEnums_SerializeAsNames_NotNumericValues()
        {
            Assert.Equal("\"RX\"", JsonConvert.SerializeObject(TarRecordingDirection.RX));
            Assert.Equal("\"TX\"", JsonConvert.SerializeObject(TarRecordingDirection.TX));
            Assert.Equal("\"InboundRadio\"", JsonConvert.SerializeObject(TarRecordingSourceType.InboundRadio));
            Assert.Equal("\"ConsoleTx\"", JsonConvert.SerializeObject(TarRecordingSourceType.ConsoleTx));
        }

        /*
        ** Assembly + reflection API lock
        */

        /// <summary>
        /// The TAR models are portable DvmConsole.Core types; the WPF app and
        /// the recording metadata writer both depend on them living in the
        /// core assembly (not the WPF project).
        /// </summary>
        [Fact]
        public void TarModels_Types_LiveInDvmConsoleCoreAssembly()
        {
            Assert.Equal("DvmConsole.Core", typeof(TarRecordingDirection).Assembly.GetName().Name);
            Assert.Equal("DvmConsole.Core", typeof(TarRecordingSourceType).Assembly.GetName().Name);
            Assert.Equal("DvmConsole.Core", typeof(TarChannelConfig).Assembly.GetName().Name);
            Assert.Equal("DvmConsole.Core", typeof(TarRecordingMetadata).Assembly.GetName().Name);
        }

        /// <summary>
        /// TarChannelConfig exposes exactly Enabled, RetentionDays and
        /// IgnoredSubscriberIds, in declaration order, all readable and
        /// writable (JSON round-trip requires setters).
        /// </summary>
        [Fact]
        public void TarChannelConfig_PropertyApi_IsLocked()
        {
            var properties = typeof(TarChannelConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            Assert.Equal(
                new[] { "Enabled", "RetentionDays", "IgnoredSubscriberIds" },
                properties.Select(p => p.Name));
            Assert.Equal(
                new[] { typeof(bool), typeof(int), typeof(List<uint>) },
                properties.Select(p => p.PropertyType));
            Assert.All(properties, p =>
            {
                Assert.True(p.CanRead, $"{p.Name} must be readable");
                Assert.True(p.CanWrite, $"{p.Name} must be writable");
            });
        }

        /// <summary>
        /// TarRecordingMetadata exposes exactly 29 public properties with the
        /// pinned names, types and declaration order. JSON key order follows
        /// this order, so renaming, reordering, adding or removing a property
        /// is a breaking on-disk metadata change.
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_PropertyApi_IsLocked()
        {
            var properties = typeof(TarRecordingMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            string[] expectedNames =
            {
                "SchemaVersion", "RecordingId", "Direction", "RecordingSourceType", "Protocol",
                "UtcStartTime", "UtcEndTime", "DurationMs",
                "FilePath", "FileName", "FileSizeBytes", "SampleRate", "BitsPerSample", "ChannelCount",
                "SystemName", "ChannelName", "TalkgroupId", "TalkgroupName", "SubscriberId", "SubscriberAlias",
                "ConsoleId", "ConsoleName",
                "IsEncrypted", "EncryptionAlgorithm", "EncryptionKeyId",
                "StreamId", "RetentionDaysAtRecordTime", "TrimLeadMs", "TrimTailMs"
            };

            Type[] expectedTypes =
            {
                typeof(int), typeof(string), typeof(TarRecordingDirection), typeof(TarRecordingSourceType), typeof(string),
                typeof(DateTime), typeof(DateTime), typeof(long),
                typeof(string), typeof(string), typeof(long), typeof(int), typeof(int), typeof(int),
                typeof(string), typeof(string), typeof(uint?), typeof(string), typeof(uint?), typeof(string),
                typeof(uint?), typeof(string),
                typeof(bool), typeof(string), typeof(ushort?),
                typeof(uint?), typeof(int?), typeof(int), typeof(int)
            };

            Assert.Equal(expectedNames.Length, properties.Length);
            Assert.Equal(expectedNames, properties.Select(p => p.Name));
            Assert.Equal(expectedTypes, properties.Select(p => p.PropertyType));
            Assert.All(properties, p =>
            {
                Assert.True(p.CanRead, $"{p.Name} must be readable");
                Assert.True(p.CanWrite, $"{p.Name} must be writable");
            });
        }

        /*
        ** TarChannelConfig defaults + JSON
        */

        /// <summary>
        /// A fresh TarChannelConfig is disabled, keeps recordings 7 days and
        /// carries a non-null, mutable subscriber-id list.
        /// </summary>
        [Fact]
        public void TarChannelConfig_Defaults_AreStableContract()
        {
            var config = new TarChannelConfig();

            Assert.False(config.Enabled);
            Assert.Equal(7, config.RetentionDays);
            Assert.NotNull(config.IgnoredSubscriberIds);
            Assert.Empty(config.IgnoredSubscriberIds);

            config.IgnoredSubscriberIds.Add(1234u);
            Assert.Equal(new List<uint> { 1234u }, config.IgnoredSubscriberIds);
        }

        /// <summary>
        /// TarChannelConfig serializes with PascalCase keys in declaration
        /// order, ignoring no properties.
        /// </summary>
        [Fact]
        public void TarChannelConfig_SerializesPascalCase_InDeclarationOrder()
        {
            var config = new TarChannelConfig
            {
                Enabled = true,
                RetentionDays = 30,
                IgnoredSubscriberIds = new List<uint> { 1001u, 42u }
            };

            Assert.Equal(
                "{\"Enabled\":true,\"RetentionDays\":30,\"IgnoredSubscriberIds\":[1001,42]}",
                JsonConvert.SerializeObject(config));
        }

        /// <summary>
        /// A full JSON round-trip preserves every TarChannelConfig value,
        /// including the ignored-subscriber id list.
        /// </summary>
        [Fact]
        public void TarChannelConfig_RoundTrip_PreservesIgnoredSubscriberIds()
        {
            var config = new TarChannelConfig
            {
                Enabled = true,
                RetentionDays = 30,
                IgnoredSubscriberIds = new List<uint> { 1001u, 42u, 7u }
            };

            var back = JsonConvert.DeserializeObject<TarChannelConfig>(JsonConvert.SerializeObject(config));

            Assert.True(back.Enabled);
            Assert.Equal(30, back.RetentionDays);
            Assert.Equal(new List<uint> { 1001u, 42u, 7u }, back.IgnoredSubscriberIds);
        }

        /*
        ** TarRecordingMetadata defaults
        */

        /// <summary>
        /// A fresh TarRecordingMetadata pins the schema/audio-format defaults,
        /// empty strings, default enum members, default DateTime values and
        /// null nullable fields.
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_Defaults_AreStableContract()
        {
            var meta = new TarRecordingMetadata();

            Assert.Equal(1, meta.SchemaVersion);
            Assert.Equal(string.Empty, meta.Protocol);
            Assert.Equal(TarRecordingDirection.RX, meta.Direction);
            Assert.Equal(TarRecordingSourceType.InboundRadio, meta.RecordingSourceType);
            Assert.Equal(default(DateTime), meta.UtcStartTime);
            Assert.Equal(default(DateTime), meta.UtcEndTime);
            Assert.Equal(0L, meta.DurationMs);
            Assert.Equal(string.Empty, meta.FilePath);
            Assert.Equal(string.Empty, meta.FileName);
            Assert.Equal(0L, meta.FileSizeBytes);
            Assert.Equal(8000, meta.SampleRate);
            Assert.Equal(16, meta.BitsPerSample);
            Assert.Equal(1, meta.ChannelCount);
            Assert.Equal(string.Empty, meta.SystemName);
            Assert.Equal(string.Empty, meta.ChannelName);
            Assert.Null(meta.TalkgroupId);
            Assert.Equal(string.Empty, meta.TalkgroupName);
            Assert.Null(meta.SubscriberId);
            Assert.Equal(string.Empty, meta.SubscriberAlias);
            Assert.Null(meta.ConsoleId);
            Assert.Equal(string.Empty, meta.ConsoleName);
            Assert.False(meta.IsEncrypted);
            Assert.Equal(string.Empty, meta.EncryptionAlgorithm);
            Assert.Null(meta.EncryptionKeyId);
            Assert.Null(meta.StreamId);
            Assert.Null(meta.RetentionDaysAtRecordTime);
            Assert.Equal(0, meta.TrimLeadMs);
            Assert.Equal(0, meta.TrimTailMs);
        }

        /// <summary>
        /// The constructor-generated RecordingId is 32 lowercase hex
        /// characters (Guid "N" format).
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_RecordingId_Is32LowercaseHexChars()
        {
            var meta = new TarRecordingMetadata();

            Assert.Matches("^[0-9a-f]{32}$", meta.RecordingId);
        }

        /// <summary>
        /// Every fresh instance gets its own non-empty RecordingId (the exact
        /// GUID value is never asserted; only shape and uniqueness).
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_RecordingId_IsUniquePerInstance()
        {
            var first = new TarRecordingMetadata();
            var second = new TarRecordingMetadata();

            Assert.False(string.IsNullOrEmpty(first.RecordingId));
            Assert.False(string.IsNullOrEmpty(second.RecordingId));
            Assert.NotEqual(first.RecordingId, second.RecordingId);
        }

        /*
        ** TarRecordingMetadata assignment + JSON
        */

        /// <summary>
        /// Deterministic fully-populated metadata used by the serialization,
        /// round-trip and assignability tests.
        /// </summary>
        private static TarRecordingMetadata BuildPopulatedMetadata()
        {
            return new TarRecordingMetadata
            {
                SchemaVersion = 2,
                RecordingId = "0123456789abcdef0123456789abcdef",
                Direction = TarRecordingDirection.TX,
                RecordingSourceType = TarRecordingSourceType.ConsoleTx,
                Protocol = "P25",
                UtcStartTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                UtcEndTime = new DateTime(2026, 1, 2, 3, 5, 5, DateTimeKind.Utc),
                DurationMs = 60000L,
                FilePath = "/recordings/2026/01/02/abc.wav",
                FileName = "abc.wav",
                FileSizeBytes = 123456L,
                SampleRate = 8000,
                BitsPerSample = 16,
                ChannelCount = 1,
                SystemName = "System A",
                ChannelName = "Channel 1",
                TalkgroupId = 1001u,
                TalkgroupName = "Dispatch",
                SubscriberId = 2002u,
                SubscriberAlias = "RADIO-2002",
                ConsoleId = 3u,
                ConsoleName = "Console West",
                IsEncrypted = true,
                EncryptionAlgorithm = "AES",
                EncryptionKeyId = 7,
                StreamId = 42u,
                RetentionDaysAtRecordTime = 30,
                TrimLeadMs = 250,
                TrimTailMs = 500
            };
        }

        /// <summary>
        /// Every TarRecordingMetadata property is assignable and reads back
        /// the assigned value (JSON round-trip requires setters; the TAR
        /// recorder requires getters).
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_AllProperties_AreAssignable()
        {
            var meta = BuildPopulatedMetadata();

            Assert.Equal(2, meta.SchemaVersion);
            Assert.Equal("0123456789abcdef0123456789abcdef", meta.RecordingId);
            Assert.Equal(TarRecordingDirection.TX, meta.Direction);
            Assert.Equal(TarRecordingSourceType.ConsoleTx, meta.RecordingSourceType);
            Assert.Equal("P25", meta.Protocol);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), meta.UtcStartTime);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 5, 5, DateTimeKind.Utc), meta.UtcEndTime);
            Assert.Equal(60000L, meta.DurationMs);
            Assert.Equal("/recordings/2026/01/02/abc.wav", meta.FilePath);
            Assert.Equal("abc.wav", meta.FileName);
            Assert.Equal(123456L, meta.FileSizeBytes);
            Assert.Equal(8000, meta.SampleRate);
            Assert.Equal(16, meta.BitsPerSample);
            Assert.Equal(1, meta.ChannelCount);
            Assert.Equal("System A", meta.SystemName);
            Assert.Equal("Channel 1", meta.ChannelName);
            Assert.Equal(1001u, meta.TalkgroupId);
            Assert.Equal("Dispatch", meta.TalkgroupName);
            Assert.Equal(2002u, meta.SubscriberId);
            Assert.Equal("RADIO-2002", meta.SubscriberAlias);
            Assert.Equal(3u, meta.ConsoleId);
            Assert.Equal("Console West", meta.ConsoleName);
            Assert.True(meta.IsEncrypted);
            Assert.Equal("AES", meta.EncryptionAlgorithm);
            Assert.Equal((ushort?)7, meta.EncryptionKeyId);
            Assert.Equal(42u, meta.StreamId);
            Assert.Equal(30, meta.RetentionDaysAtRecordTime);
            Assert.Equal(250, meta.TrimLeadMs);
            Assert.Equal(500, meta.TrimTailMs);
        }

        /// <summary>
        /// A populated metadata object serializes with Formatting.Indented to
        /// PascalCase JSON: representative values, enum member names,
        /// null nullable fields, and exact property (declaration) order.
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_PopulatedObject_SerializesPascalCaseInDeclarationOrder()
        {
            var meta = BuildPopulatedMetadata();

            var obj = JObject.Parse(JsonConvert.SerializeObject(meta, Formatting.Indented));

            // Representative exact values.
            Assert.Equal(2, (int)obj["SchemaVersion"]);
            Assert.Equal("0123456789abcdef0123456789abcdef", (string)obj["RecordingId"]);
            Assert.Equal("TX", (string)obj["Direction"]);
            Assert.Equal("ConsoleTx", (string)obj["RecordingSourceType"]);
            Assert.Equal("P25", (string)obj["Protocol"]);
            Assert.Equal(60000L, (long)obj["DurationMs"]);
            Assert.Equal(8000, (int)obj["SampleRate"]);
            Assert.Equal(16, (int)obj["BitsPerSample"]);
            Assert.Equal(1, (int)obj["ChannelCount"]);
            Assert.Equal(123456L, (long)obj["FileSizeBytes"]);
            Assert.Equal("Dispatch", (string)obj["TalkgroupName"]);
            Assert.Equal(2002u, (uint)obj["SubscriberId"]);
            Assert.True((bool)obj["IsEncrypted"]);
            Assert.Equal("AES", (string)obj["EncryptionAlgorithm"]);
            Assert.Equal(7, (int)obj["EncryptionKeyId"]);
            Assert.Equal(42u, (uint)obj["StreamId"]);
            Assert.Equal(30, (int)obj["RetentionDaysAtRecordTime"]);
            Assert.Equal(500, (int)obj["TrimTailMs"]);
            Assert.Equal(meta.UtcStartTime, (DateTime)obj["UtcStartTime"]);
            Assert.Equal(meta.UtcEndTime, (DateTime)obj["UtcEndTime"]);

            // Null nullable fields serialize as explicit JSON null.
            var withNulls = new TarRecordingMetadata
            {
                TalkgroupId = null,
                SubscriberId = null,
                ConsoleId = null,
                EncryptionKeyId = null,
                StreamId = null,
                RetentionDaysAtRecordTime = null
            };
            var nullObj = JObject.Parse(JsonConvert.SerializeObject(withNulls, Formatting.Indented));
            Assert.Equal(JTokenType.Null, nullObj["TalkgroupId"].Type);
            Assert.Equal(JTokenType.Null, nullObj["SubscriberId"].Type);
            Assert.Equal(JTokenType.Null, nullObj["ConsoleId"].Type);
            Assert.Equal(JTokenType.Null, nullObj["EncryptionKeyId"].Type);
            Assert.Equal(JTokenType.Null, nullObj["StreamId"].Type);
            Assert.Equal(JTokenType.Null, nullObj["RetentionDaysAtRecordTime"].Type);

            // JSON key order is the declaration order (metadata contract).
            string[] expectedOrder =
            {
                "SchemaVersion", "RecordingId", "Direction", "RecordingSourceType", "Protocol",
                "UtcStartTime", "UtcEndTime", "DurationMs",
                "FilePath", "FileName", "FileSizeBytes", "SampleRate", "BitsPerSample", "ChannelCount",
                "SystemName", "ChannelName", "TalkgroupId", "TalkgroupName", "SubscriberId", "SubscriberAlias",
                "ConsoleId", "ConsoleName",
                "IsEncrypted", "EncryptionAlgorithm", "EncryptionKeyId",
                "StreamId", "RetentionDaysAtRecordTime", "TrimLeadMs", "TrimTailMs"
            };
            Assert.Equal(expectedOrder, obj.Properties().Select(p => p.Name));
        }

        /// <summary>
        /// A full JSON round-trip preserves every populated metadata value,
        /// including enum members (as names, not numbers).
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_RoundTrip_PreservesValuesAndEnumNames()
        {
            var meta = BuildPopulatedMetadata();

            var back = JsonConvert.DeserializeObject<TarRecordingMetadata>(JsonConvert.SerializeObject(meta));

            Assert.Equal(2, back.SchemaVersion);
            Assert.Equal("0123456789abcdef0123456789abcdef", back.RecordingId);
            Assert.Equal(TarRecordingDirection.TX, back.Direction);
            Assert.Equal(TarRecordingSourceType.ConsoleTx, back.RecordingSourceType);
            Assert.Equal("P25", back.Protocol);
            Assert.Equal(meta.UtcStartTime, back.UtcStartTime);
            Assert.Equal(meta.UtcEndTime, back.UtcEndTime);
            Assert.Equal(60000L, back.DurationMs);
            Assert.Equal("/recordings/2026/01/02/abc.wav", back.FilePath);
            Assert.Equal("abc.wav", back.FileName);
            Assert.Equal(123456L, back.FileSizeBytes);
            Assert.Equal(8000, back.SampleRate);
            Assert.Equal(16, back.BitsPerSample);
            Assert.Equal(1, back.ChannelCount);
            Assert.Equal("System A", back.SystemName);
            Assert.Equal("Channel 1", back.ChannelName);
            Assert.Equal(1001u, back.TalkgroupId);
            Assert.Equal("Dispatch", back.TalkgroupName);
            Assert.Equal(2002u, back.SubscriberId);
            Assert.Equal("RADIO-2002", back.SubscriberAlias);
            Assert.Equal(3u, back.ConsoleId);
            Assert.Equal("Console West", back.ConsoleName);
            Assert.True(back.IsEncrypted);
            Assert.Equal("AES", back.EncryptionAlgorithm);
            Assert.Equal((ushort?)7, back.EncryptionKeyId);
            Assert.Equal(42u, back.StreamId);
            Assert.Equal(30, back.RetentionDaysAtRecordTime);
            Assert.Equal(250, back.TrimLeadMs);
            Assert.Equal(500, back.TrimTailMs);
        }

        /// <summary>
        /// Unknown JSON fields are ignored on deserialization; fields absent
        /// from the JSON keep their constructor defaults.
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_Deserialize_IgnoresUnknownFieldsAndKeepsDefaults()
        {
            const string json =
                "{\"SchemaVersion\":3,\"UnknownField\":\"whatever\",\"Direction\":\"TX\",\"SampleRate\":48000}";

            var meta = JsonConvert.DeserializeObject<TarRecordingMetadata>(json);

            Assert.Equal(3, meta.SchemaVersion);
            Assert.Equal(TarRecordingDirection.TX, meta.Direction);
            Assert.Equal(48000, meta.SampleRate);

            // Fields not present in the JSON keep constructor defaults.
            Assert.Equal(TarRecordingSourceType.InboundRadio, meta.RecordingSourceType);
            Assert.Equal(string.Empty, meta.Protocol);
            Assert.Equal(string.Empty, meta.FilePath);
            Assert.Equal(16, meta.BitsPerSample);
            Assert.Equal(1, meta.ChannelCount);
            Assert.Null(meta.TalkgroupId);
            Assert.Null(meta.SubscriberId);
            Assert.Null(meta.EncryptionKeyId);
            Assert.Null(meta.RetentionDaysAtRecordTime);
            Assert.False(meta.IsEncrypted);
            Assert.Equal(0, meta.TrimLeadMs);
            Assert.Equal(0, meta.TrimTailMs);
        }

        /// <summary>
        /// Deserializing a minimal empty object yields a fully valid metadata
        /// record built from constructor defaults, including a fresh
        /// constructor-generated RecordingId.
        /// </summary>
        [Fact]
        public void TarRecordingMetadata_DeserializeEmptyObject_PreservesConstructorDefaults()
        {
            var meta = JsonConvert.DeserializeObject<TarRecordingMetadata>("{}");

            Assert.NotNull(meta);
            Assert.Equal(1, meta.SchemaVersion);
            Assert.Equal(8000, meta.SampleRate);
            Assert.Equal(16, meta.BitsPerSample);
            Assert.Equal(1, meta.ChannelCount);
            Assert.Equal(string.Empty, meta.Protocol);
            Assert.Equal(string.Empty, meta.FilePath);
            Assert.Equal(string.Empty, meta.FileName);
            Assert.Equal(TarRecordingDirection.RX, meta.Direction);
            Assert.Equal(TarRecordingSourceType.InboundRadio, meta.RecordingSourceType);
            Assert.Equal(default(DateTime), meta.UtcStartTime);
            Assert.Null(meta.TalkgroupId);
            Assert.Null(meta.SubscriberId);
            Assert.Null(meta.EncryptionKeyId);
            Assert.Null(meta.StreamId);
            Assert.False(meta.IsEncrypted);
            Assert.Equal(0, meta.TrimLeadMs);
            Assert.Equal(0, meta.TrimTailMs);
            Assert.Matches("^[0-9a-f]{32}$", meta.RecordingId);
        }
    }
}
