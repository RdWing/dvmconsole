// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Input;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the pure persisted-WPF-key to portable gesture
    /// mapping seam. This decodes the raw integer stored by
    /// UserSettingsPttSection; persistence and shell registration remain
    /// separate boundaries.
    /// </summary>
    public sealed class PersistedHotkeyMapperTests
    {
        [Fact]
        public void Mapper_HasOnlyThePersistedIntegerTryMapSurface()
        {
            var type = typeof(PersistedHotkeyMapper);

            Assert.True(type.IsClass);
            Assert.True(type.IsAbstract);
            Assert.True(type.IsSealed);
            Assert.Equal("DvmConsole.Avalonia.Input", type.Namespace);
            Assert.Same(typeof(KeyGestureMapper).Assembly, type.Assembly);

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .ToArray();

            var method = Assert.Single(methods);
            Assert.Equal("TryMap", method.Name);
            Assert.Equal(typeof(bool), method.ReturnType);
            Assert.Equal(
                new[] { typeof(int), typeof(HotkeyGesture).MakeByRefType() },
                method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            Assert.True(method.GetParameters()[1].IsOut);
        }

        [Theory]
        [InlineData(0x41, HotkeyKey.A)]
        [InlineData(0x5A, HotkeyKey.Z)]
        [InlineData(0x30, HotkeyKey.D0)]
        [InlineData(0x39, HotkeyKey.D9)]
        [InlineData(0x70, HotkeyKey.F1)]
        [InlineData(0x87, HotkeyKey.F24)]
        [InlineData(0x0D, HotkeyKey.Enter)]
        [InlineData(0x1B, HotkeyKey.Escape)]
        [InlineData(0x09, HotkeyKey.Tab)]
        [InlineData(0x20, HotkeyKey.Space)]
        [InlineData(0x08, HotkeyKey.Backspace)]
        [InlineData(0x2E, HotkeyKey.Delete)]
        [InlineData(0x2D, HotkeyKey.Insert)]
        [InlineData(0x24, HotkeyKey.Home)]
        [InlineData(0x23, HotkeyKey.End)]
        [InlineData(0x21, HotkeyKey.PageUp)]
        [InlineData(0x22, HotkeyKey.PageDown)]
        [InlineData(0x25, HotkeyKey.Left)]
        [InlineData(0x27, HotkeyKey.Right)]
        [InlineData(0x26, HotkeyKey.Up)]
        [InlineData(0x28, HotkeyKey.Down)]
        public void TryMap_SupportedWpfKeyCode_MapsToGesture(int persistedKeys, HotkeyKey expectedKey)
        {
            var mapped = PersistedHotkeyMapper.TryMap(persistedKeys, out var gesture);

            Assert.True(mapped);
            Assert.Equal(expectedKey, gesture.Key);
            Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        }

        [Theory]
        [InlineData(0x20041, HotkeyKey.A, HotkeyModifiers.Control)]
        [InlineData(0x1001B, HotkeyKey.Escape, HotkeyModifiers.Shift)]
        [InlineData(0x50070, HotkeyKey.F1, HotkeyModifiers.Alt | HotkeyModifiers.Shift)]
        public void TryMap_WpfModifierBits_MapsToPortableModifiers(
            int persistedKeys,
            HotkeyKey expectedKey,
            HotkeyModifiers expectedModifiers)
        {
            var mapped = PersistedHotkeyMapper.TryMap(persistedKeys, out var gesture);

            Assert.True(mapped);
            Assert.Equal(expectedKey, gesture.Key);
            Assert.Equal(expectedModifiers, gesture.Modifiers);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0x10000)]
        [InlineData(0x60)]
        [InlineData(0x4000C0)]
        [InlineData(unchecked((int)0x80010041))]
        public void TryMap_UnsupportedOrInvalidValue_ReturnsFalseAndZeroGesture(int persistedKeys)
        {
            var mapped = PersistedHotkeyMapper.TryMap(persistedKeys, out var gesture);

            Assert.False(mapped);
            Assert.Equal(HotkeyKey.None, gesture.Key);
            Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        }
    }
}
