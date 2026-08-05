// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic contract tests for the portable key container
* (DvmConsole.Core/Configuration/KeyContainer.cs, namespace dvmconsole).
* These lock the DTO defaults, the KeyBytes hex-decoding contract
* (synthetic test values only - never real key material), the public API
* shape, and the exact YamlDotNet 16.2.0 binding behavior under the
* production deserializer settings (CamelCaseNamingConvention with
* IgnoreUnmatchedProperties). All behaviors below were verified
* empirically against YamlDotNet 16.2.0 before being encoded here.
*/
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="KeyContainer"/> and
    /// <see cref="KeyEntry"/>.
    /// </summary>
    public class KeyContainerTests
    {
        /*
        ** DTO defaults and mutability
        */

        /// <summary>
        /// A fresh KeyContainer exposes a non-null, empty, mutable Keys
        /// list via its public parameterless constructor (the production
        /// property initializer "= []" must survive the port).
        /// </summary>
        [Fact]
        public void KeyContainer_New_KeysIsNonNullEmptyAndMutable()
        {
            var container = new KeyContainer();

            Assert.NotNull(container.Keys);
            Assert.Empty(container.Keys);

            container.Keys.Add(new KeyEntry { KeyId = 1 });
            Assert.Single(container.Keys);
        }

        /// <summary>
        /// Both DTO types expose a public parameterless constructor,
        /// callable from this (separate) test assembly.
        /// </summary>
        [Fact]
        public void KeyContainerAndKeyEntry_PublicParameterlessConstructors()
        {
            Assert.True(typeof(KeyContainer).GetConstructor(Type.EmptyTypes)?.IsPublic);
            Assert.True(typeof(KeyEntry).GetConstructor(Type.EmptyTypes)?.IsPublic);
        }

        /// <summary>
        /// A fresh KeyEntry defaults to KeyId=0, AlgId=0, and a null Key.
        /// </summary>
        [Fact]
        public void KeyEntry_Defaults_AreZeroZeroNull()
        {
            var entry = new KeyEntry();

            Assert.Equal(0, entry.KeyId);
            Assert.Equal(0, entry.AlgId);
            Assert.Null(entry.Key);
        }

        /// <summary>
        /// All three properties have public setters that preserve the
        /// assigned values (KeyId is ushort, AlgId is int).
        /// </summary>
        [Fact]
        public void KeyEntry_Setters_PreserveValues()
        {
            var entry = new KeyEntry { KeyId = 17, AlgId = 128, Key = "A1B2C3D4" };

            Assert.Equal(17, entry.KeyId);
            Assert.Equal(128, entry.AlgId);
            Assert.Equal("A1B2C3D4", entry.Key);
        }

        /*
        ** KeyBytes hex decoding
        */

        /// <summary>
        /// A null or empty Key decodes to an empty byte[] without throwing.
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_NullAndEmptyKey_ReturnsEmptyArray()
        {
            Assert.Empty(new KeyEntry { Key = null }.KeyBytes);
            Assert.Empty(new KeyEntry { Key = "" }.KeyBytes);
        }

        /// <summary>
        /// KeyBytes is an expression-bodied get-only property: every access
        /// re-decodes and returns a fresh array instance whose contents are
        /// sequence-equal to every other access.
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_TwoAccesses_ReturnFreshEqualArrays()
        {
            var entry = new KeyEntry { Key = "A1B2C3D4" };

            var first = entry.KeyBytes;
            var second = entry.KeyBytes;

            Assert.NotSame(first, second);
            Assert.Equal(first, second);
        }

        /// <summary>
        /// Hex decoding is case-insensitive: uppercase, lowercase, and mixed
        /// forms of the same key all decode to the same synthetic bytes.
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_DecodesUpperLowerAndMixedCaseHex()
        {
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, new KeyEntry { Key = "A1B2C3D4" }.KeyBytes);
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, new KeyEntry { Key = "a1b2c3d4" }.KeyBytes);
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, new KeyEntry { Key = "A1b2C3d4" }.KeyBytes);
        }

        /// <summary>
        /// Leading zeros are significant: "0011" decodes to two bytes
        /// (0x00, 0x11), not a single byte 0x11.
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_PreservesLeadingZeros()
        {
            Assert.Equal(new byte[] { 0x00, 0x11 }, new KeyEntry { Key = "0011" }.KeyBytes);
        }

        /// <summary>
        /// Odd-length hex strings throw ArgumentOutOfRangeException: the
        /// decoder's Substring(x, 2) cannot extract a second byte from a
        /// trailing lone nibble. Locked on the current implementation's
        /// exception type ("0" and "A1B" both fail on the final byte).
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_OddLengthHex_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new KeyEntry { Key = "0" }.KeyBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => new KeyEntry { Key = "A1B" }.KeyBytes);
        }

        /// <summary>
        /// Non-hex characters, a "0x" prefix, and interior whitespace all
        /// fail Convert.ToByte's base-16 parse and surface as FormatException.
        /// </summary>
        [Fact]
        public void KeyEntry_KeyBytes_InvalidHex_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => new KeyEntry { Key = "ZZ" }.KeyBytes);
            Assert.Throws<FormatException>(() => new KeyEntry { Key = "0x11" }.KeyBytes);
            Assert.Throws<FormatException>(() => new KeyEntry { Key = "A1 B2" }.KeyBytes);
        }

        /*
        ** KeyBytes API shape (reflection)
        */

        /// <summary>
        /// KeyBytes is a public, get-only byte[] property - part of the
        /// serialization-facing surface that WPF key loading depends on.
        /// </summary>
        [Fact]
        public void KeyEntry_ApiShape_KeyBytesIsPublicGetOnlyByteArray()
        {
            var property = typeof(KeyEntry).GetProperty(nameof(KeyEntry.KeyBytes));

            Assert.NotNull(property);
            Assert.Equal(typeof(byte[]), property.PropertyType);
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod.IsPublic);
            Assert.Null(property.SetMethod);
        }

        /// <summary>
        /// The YAML-bound scalar properties keep their exact types
        /// (KeyId: ushort, AlgId: int, Key: string) and public setters so
        /// YamlDotNet can bind them.
        /// </summary>
        [Fact]
        public void KeyEntry_ApiShape_ScalarPropertiesHaveContractTypesAndPublicSetters()
        {
            var keyId = typeof(KeyEntry).GetProperty(nameof(KeyEntry.KeyId));
            var algId = typeof(KeyEntry).GetProperty(nameof(KeyEntry.AlgId));
            var key = typeof(KeyEntry).GetProperty(nameof(KeyEntry.Key));

            Assert.Equal(typeof(ushort), keyId.PropertyType);
            Assert.Equal(typeof(int), algId.PropertyType);
            Assert.Equal(typeof(string), key.PropertyType);

            foreach (var property in new[] { keyId, algId, key })
            {
                Assert.NotNull(property.SetMethod);
                Assert.True(property.SetMethod.IsPublic);
            }
        }

        /*
        ** YAML compatibility (production deserializer settings)
        */

        /// <summary>
        /// Inline camelCase YAML ("keyId"/"algId"/"key") deserializes under
        /// the exact production convention (CamelCaseNamingConvention with
        /// IgnoreUnmatchedProperties, as used by the WPF key loader), and an
        /// unknown field is silently ignored. 0x0011 is a synthetic test
        /// key id, never real key material.
        /// </summary>
        [Fact]
        public void Deserialize_CamelCaseYaml_BindsKeyIdAlgIdAndKey()
        {
            const string yaml = """
                keys:
                  - keyId: 0x0011
                    algId: 0x80
                    key: A1B2C3D4
                    unknownField: ignored
                """;

            var container = Deserialize(yaml);

            var entry = Assert.Single(container.Keys);
            Assert.Equal(0x0011, entry.KeyId);
            Assert.Equal(0x80, entry.AlgId);
            Assert.Equal("A1B2C3D4", entry.Key);
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, entry.KeyBytes);
        }

        /// <summary>
        /// PascalCase YAML keys ("KeyId:"/"AlgId:"/"Key:") do NOT bind under
        /// the production camelCase convention - empirically verified against
        /// YamlDotNet 16.2.0: unmatched members are left at their defaults.
        /// The key file format is camelCase, so this locks in that exact
        /// convention rather than a lenient one.
        /// </summary>
        [Fact]
        public void Deserialize_PascalCaseYaml_DoesNotBindUnderCamelCaseConvention()
        {
            const string yaml = """
                keys:
                  - KeyId: 0x0011
                    AlgId: 0x80
                    Key: A1B2C3D4
                """;

            var container = Deserialize(yaml);

            var entry = Assert.Single(container.Keys);
            Assert.Equal(0, entry.KeyId);
            Assert.Equal(0, entry.AlgId);
            Assert.Null(entry.Key);
            Assert.Empty(entry.KeyBytes);
        }

        /// <summary>
        /// A document without a "keys" member leaves Keys at the value
        /// produced by the parameterless constructor (YamlDotNet never
        /// nulls an unbound member), so the DTO initializer's non-null
        /// empty list is what callers observe.
        /// </summary>
        [Fact]
        public void Deserialize_MissingKeys_YieldsDefaultKeysList()
        {
            var container = Deserialize("other: 1");

            Assert.NotNull(container.Keys);
            Assert.Empty(container.Keys);
        }

        /// <summary>
        /// Multiple entries deserialize in document order with their exact
        /// scalar values, exercising both synthetic key payloads.
        /// </summary>
        [Fact]
        public void Deserialize_MultipleEntries_PreserveOrderAndValues()
        {
            const string yaml = """
                keys:
                  - keyId: 0x0001
                    algId: 0x02
                    key: 0011
                  - keyId: 0x0003
                    algId: 0x04
                    key: deadbeef
                """;

            var container = Deserialize(yaml);

            Assert.Equal(2, container.Keys.Count);
            Assert.Equal(0x0001, container.Keys[0].KeyId);
            Assert.Equal(0x02, container.Keys[0].AlgId);
            Assert.Equal("0011", container.Keys[0].Key);
            Assert.Equal(new byte[] { 0x00, 0x11 }, container.Keys[0].KeyBytes);
            Assert.Equal(0x0003, container.Keys[1].KeyId);
            Assert.Equal(0x04, container.Keys[1].AlgId);
            Assert.Equal("deadbeef", container.Keys[1].Key);
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, container.Keys[1].KeyBytes);
        }

        /// <summary>
        /// The deserializer is built with the exact production settings the
        /// WPF key loader uses (MainWindow.KeyRequests.cs): camelCase naming
        /// with unmatched properties ignored.
        /// </summary>
        private static KeyContainer Deserialize(string yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            return deserializer.Deserialize<KeyContainer>(yaml);
        }
    }
}
