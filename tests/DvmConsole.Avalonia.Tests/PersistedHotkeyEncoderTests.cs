// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Input;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the pure portable-gesture to persisted WPF Keys
    /// integer encoder. Two-way settings wiring remains a later composition
    /// seam.
    /// </summary>
    public sealed class PersistedHotkeyEncoderTests
    {
        [Fact]
        public void Encoder_HasOnlyTheGestureTryMapSurface()
        {
            var type = typeof(PersistedHotkeyEncoder);

            Assert.True(type.IsClass);
            Assert.True(type.IsAbstract);
            Assert.True(type.IsSealed);
            Assert.Equal("DvmConsole.Avalonia.Input", type.Namespace);
            Assert.Same(typeof(PersistedHotkeyMapper).Assembly, type.Assembly);

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .ToArray();

            var method = Assert.Single(methods);
            Assert.Equal("TryMap", method.Name);
            Assert.Equal(typeof(bool), method.ReturnType);
            Assert.Equal(
                new[] { typeof(HotkeyGesture), typeof(int).MakeByRefType() },
                method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
            Assert.True(method.GetParameters()[1].IsOut);
        }

        [Theory]
        [InlineData(HotkeyKey.A, HotkeyModifiers.None, 0x41)]
        [InlineData(HotkeyKey.Z, HotkeyModifiers.None, 0x5A)]
        [InlineData(HotkeyKey.D0, HotkeyModifiers.None, 0x30)]
        [InlineData(HotkeyKey.D9, HotkeyModifiers.None, 0x39)]
        [InlineData(HotkeyKey.F1, HotkeyModifiers.None, 0x70)]
        [InlineData(HotkeyKey.F24, HotkeyModifiers.None, 0x87)]
        [InlineData(HotkeyKey.Enter, HotkeyModifiers.None, 0x0D)]
        [InlineData(HotkeyKey.Escape, HotkeyModifiers.None, 0x1B)]
        [InlineData(HotkeyKey.Tab, HotkeyModifiers.None, 0x09)]
        [InlineData(HotkeyKey.Space, HotkeyModifiers.None, 0x20)]
        [InlineData(HotkeyKey.Backspace, HotkeyModifiers.None, 0x08)]
        [InlineData(HotkeyKey.Delete, HotkeyModifiers.None, 0x2E)]
        [InlineData(HotkeyKey.Insert, HotkeyModifiers.None, 0x2D)]
        [InlineData(HotkeyKey.Home, HotkeyModifiers.None, 0x24)]
        [InlineData(HotkeyKey.End, HotkeyModifiers.None, 0x23)]
        [InlineData(HotkeyKey.PageUp, HotkeyModifiers.None, 0x21)]
        [InlineData(HotkeyKey.PageDown, HotkeyModifiers.None, 0x22)]
        [InlineData(HotkeyKey.Left, HotkeyModifiers.None, 0x25)]
        [InlineData(HotkeyKey.Right, HotkeyModifiers.None, 0x27)]
        [InlineData(HotkeyKey.Up, HotkeyModifiers.None, 0x26)]
        [InlineData(HotkeyKey.Down, HotkeyModifiers.None, 0x28)]
        public void TryMap_SupportedGesture_EncodesWpfKeyCode(
            HotkeyKey key,
            HotkeyModifiers modifiers,
            int expected)
        {
            var mapped = PersistedHotkeyEncoder.TryMap(new HotkeyGesture(key, modifiers), out var persistedKeys);

            Assert.True(mapped);
            Assert.Equal(expected, persistedKeys);
        }

        [Theory]
        [InlineData(HotkeyKey.A, HotkeyModifiers.Control, 0x20041)]
        [InlineData(HotkeyKey.Escape, HotkeyModifiers.Shift, 0x1001B)]
        [InlineData(HotkeyKey.F1, HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x50070)]
        public void TryMap_SupportedModifiers_EncodesExactWpfBits(
            HotkeyKey key,
            HotkeyModifiers modifiers,
            int expected)
        {
            var mapped = PersistedHotkeyEncoder.TryMap(new HotkeyGesture(key, modifiers), out var persistedKeys);

            Assert.True(mapped);
            Assert.Equal(expected, persistedKeys);
        }

        [Theory]
        [InlineData(HotkeyKey.None, HotkeyModifiers.None)]
        [InlineData(HotkeyKey.A, HotkeyModifiers.Meta)]
        [InlineData(HotkeyKey.A, (HotkeyModifiers)16)]
        public void TryMap_UnsupportedGesture_ReturnsFalseAndZero(
            HotkeyKey key,
            HotkeyModifiers modifiers)
        {
            var mapped = PersistedHotkeyEncoder.TryMap(new HotkeyGesture(key, modifiers), out var persistedKeys);

            Assert.False(mapped);
            Assert.Equal(0, persistedKeys);
        }
    }
}
