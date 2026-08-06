// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the pure managed pointer interpreter that maps
* Avalonia PTT-button pointer updates onto the PTT down/up contract:
* DvmConsole.Avalonia.Input.PttButtonPointerInterpreter. These facts are
* written entirely against the agreed contract:
* TryGetPttPointerAction(Avalonia.Input.PointerUpdateKind, out bool)
* initializes isDown to false on every call and returns true with
* isDown=true only for PointerUpdateKind.LeftButtonPressed, true with
* isDown=false only for PointerUpdateKind.LeftButtonReleased, and false
* with isDown=false for every other kind.
*
* The interpreter is a pure translation layer: modifiers are not an input,
* and no UI, window, display, pointer event, click count, e.Handled,
* FNE/audio/network, persistence, or secret is involved. The tests are
* fully headless and use only the real Avalonia 11.3.18 PointerUpdateKind
* enum. Enum member names were verified against the installed Avalonia
* 11.3.18 reference assembly (11 members); PointerUpdateKind has no "None"
* member in 11.3.18, so the exhaustive enum test below covers every real
* member, and a future member would be caught by that test as a contract
* violation until the contract explicitly accepts it.
*
* RED contract gate: the production interpreter does not exist yet, so this
* project fails to compile until the next slice implements it.
*/
#nullable enable
using System.Reflection;
using Avalonia.Input;
using DvmConsole.Avalonia.Input;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>PttButtonPointerInterpreter</c> against the
    /// pointer-update-to-PTT contract.
    /// </summary>
    public sealed class PttButtonPointerInterpreterTests
    {
        // ---- Public shape gate -------------------------------------------------

        /// <summary>
        /// The production type is a public static class in the agreed
        /// namespace, exposing exactly one public static method:
        /// TryGetPttPointerAction(PointerUpdateKind, out bool) returning
        /// bool, with no other public members.
        /// </summary>
        [Fact]
        public void Shape_PublicStaticClass_SingleMethod_AgreedSignature()
        {
            var type = typeof(PttButtonPointerInterpreter);

            Assert.True(type.IsPublic);
            Assert.True(type.IsAbstract && type.IsSealed, "must be a static class");
            Assert.Equal("DvmConsole.Avalonia.Input", type.Namespace);

            var publicMembers = type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.Single(publicMembers);

            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.Single(methods);

            var method = methods[0];
            Assert.Equal("TryGetPttPointerAction", method.Name);
            Assert.Equal(typeof(bool), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.Equal(2, parameters.Length);
            Assert.Equal(typeof(PointerUpdateKind), parameters[0].ParameterType);
            Assert.False(parameters[0].ParameterType.IsByRef, "kind is passed by value");
            Assert.True(parameters[1].ParameterType.IsByRef, "isDown is passed by reference");
            Assert.Equal(typeof(bool), parameters[1].ParameterType.GetElementType());
            Assert.True(parameters[1].IsOut, "isDown is an out parameter");
        }

        // ---- Accepted kinds ------------------------------------------------------

        /// <summary>
        /// LeftButtonPressed is the only kind accepted as a PTT press: the
        /// call returns true and isDown comes back true, overwriting any
        /// caller-seeded value.
        /// </summary>
        [Fact]
        public void LeftButtonPressed_ReturnsTrue_IsDownTrue()
        {
            bool isDown = false;

            var accepted = PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonPressed, out isDown);

            Assert.True(accepted);
            Assert.True(isDown);
        }

        /// <summary>
        /// LeftButtonReleased is the only kind accepted as a PTT release:
        /// the call returns true and isDown comes back false, overwriting
        /// any caller-seeded value.
        /// </summary>
        [Fact]
        public void LeftButtonReleased_ReturnsTrue_IsDownFalse()
        {
            bool isDown = true;

            var accepted = PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonReleased, out isDown);

            Assert.True(accepted);
            Assert.False(isDown);
        }

        // ---- Rejected kinds -------------------------------------------------------

        /// <summary>
        /// Every non-left-button pointer update is rejected: the call
        /// returns false and isDown is initialized back to false, even when
        /// the caller pre-seeded it true.
        /// </summary>
        [Theory]
        [InlineData(PointerUpdateKind.RightButtonPressed)]
        [InlineData(PointerUpdateKind.RightButtonReleased)]
        [InlineData(PointerUpdateKind.MiddleButtonPressed)]
        [InlineData(PointerUpdateKind.MiddleButtonReleased)]
        [InlineData(PointerUpdateKind.XButton1Pressed)]
        [InlineData(PointerUpdateKind.XButton1Released)]
        [InlineData(PointerUpdateKind.XButton2Pressed)]
        [InlineData(PointerUpdateKind.XButton2Released)]
        [InlineData(PointerUpdateKind.Other)]
        public void RejectedKind_ReturnsFalse_IsDownZeroed(PointerUpdateKind kind)
        {
            bool isDown = true;

            var accepted = PttButtonPointerInterpreter.TryGetPttPointerAction(kind, out isDown);

            Assert.False(accepted);
            Assert.False(isDown);
        }

        // ---- Exhaustive contract over the real enum --------------------------------

        /// <summary>
        /// Every member of the real Avalonia 11.3.18 PointerUpdateKind enum
        /// (11 members; there is no "None") maps per contract: only
        /// LeftButtonPressed accepts with isDown=true, only
        /// LeftButtonReleased accepts with isDown=false, and every other
        /// member is rejected with isDown=false. This also guards the
        /// "only" direction of the contract and would flag any future enum
        /// member as a contract violation.
        /// </summary>
        [Fact]
        public void EveryEnumMember_MapsPerContract()
        {
            foreach (var kind in Enum.GetValues<PointerUpdateKind>())
            {
                var accepted = PttButtonPointerInterpreter.TryGetPttPointerAction(
                    kind, out var isDown);

                switch (kind)
                {
                    case PointerUpdateKind.LeftButtonPressed:
                        Assert.True(accepted);
                        Assert.True(isDown);
                        break;
                    case PointerUpdateKind.LeftButtonReleased:
                        Assert.True(accepted);
                        Assert.False(isDown);
                        break;
                    default:
                        Assert.False(accepted);
                        Assert.False(isDown);
                        break;
                }
            }
        }

        // ---- Determinism --------------------------------------------------------------

        /// <summary>
        /// The interpreter is a pure function: repeating the same accepted
        /// press yields the same accepted result with isDown=true.
        /// </summary>
        [Fact]
        public void RepeatedPressedCalls_Deterministic()
        {
            Assert.True(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonPressed, out var first));
            Assert.True(first);

            Assert.True(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonPressed, out var second));
            Assert.True(second);
        }

        /// <summary>
        /// Repeating the same rejected update yields the same rejected
        /// result with isDown=false every time.
        /// </summary>
        [Fact]
        public void RepeatedRejectedCalls_Deterministic()
        {
            Assert.False(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.MiddleButtonPressed, out var first));
            Assert.False(first);

            Assert.False(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.MiddleButtonPressed, out var second));
            Assert.False(second);
        }

        /// <summary>
        /// Interleaved press/release/press calls stay deterministic and
        /// never carry state between calls: each result depends only on the
        /// current kind.
        /// </summary>
        [Fact]
        public void InterleavedPressRelease_NoStateCarriedBetweenCalls()
        {
            Assert.True(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonPressed, out var first));
            Assert.True(first);

            Assert.True(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonReleased, out var second));
            Assert.False(second);

            Assert.True(PttButtonPointerInterpreter.TryGetPttPointerAction(
                PointerUpdateKind.LeftButtonPressed, out var third));
            Assert.True(third);
        }
    }
}
