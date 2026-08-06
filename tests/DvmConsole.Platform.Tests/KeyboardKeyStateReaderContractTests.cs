// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for the dependency-free physical-key-state probe used
    /// by the PTT hotkey watchdog. The interface carries no platform
    /// implementation or lifetime surface.
    /// </summary>
    public sealed class KeyboardKeyStateReaderContractTests
    {
        [Fact]
        public void ApiShape_IsExactAndDependencyFree()
        {
            var type = typeof(IKeyboardKeyStateReader);

            Assert.True(type.IsInterface);
            Assert.Equal("DvmConsole.Platform.Hotkeys", type.Namespace);
            Assert.False(typeof(IDisposable).IsAssignableFrom(type));

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(method => method.Name)
                .ToArray();

            var method = Assert.Single(methods);
            Assert.Equal("IsKeyDown", method.Name);
            Assert.Equal(typeof(bool), method.ReturnType);
            Assert.Equal(new[] { typeof(HotkeyGesture) },
                Array.ConvertAll(method.GetParameters(), parameter => parameter.ParameterType));
        }
    }
}
