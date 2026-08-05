// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the dependency-free unavailable global-hotkey
* fallback (the IGlobalHotkeyService implementation used when no OS-level
* global hotkey provider is available). These facts are written entirely
* against the agreed contract: every gesture is reported Unsupported,
* registration fails with HotkeyRegistrationStatus.Unsupported while
* preserving the gesture, unregistration is a no-op that always completes,
* no OS event source exists so HotkeyPressed never fires, and Dispose is
* idempotent without impairing the service afterwards.
*/
#nullable enable
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for <c>UnavailableGlobalHotkeyService</c> against the
    /// <see cref="IGlobalHotkeyService"/> interface.
    /// </summary>
    public sealed class UnavailableGlobalHotkeyServiceTests
    {
        private static readonly HotkeyGesture Gesture =
            new(HotkeyKey.Space, HotkeyModifiers.Control);

        private static UnavailableGlobalHotkeyService CreateService() => new();

        /// <summary>
        /// The fallback is a full <see cref="IGlobalHotkeyService"/>; callers
        /// can swap it in wherever the interface is expected.
        /// </summary>
        [Fact]
        public void ImplementsIGlobalHotkeyService()
        {
            IGlobalHotkeyService service = CreateService();

            Assert.NotNull(service);
        }

        /// <summary>
        /// Without an OS event source there is nothing to register against, so
        /// every gesture is reported Unsupported.
        /// </summary>
        [Theory]
        [InlineData(HotkeyKey.Space, HotkeyModifiers.Control)]
        [InlineData(HotkeyKey.None, HotkeyModifiers.None)]
        [InlineData(HotkeyKey.F12, HotkeyModifiers.Meta | HotkeyModifiers.Shift)]
        public void GetCapability_ReturnsUnsupported(HotkeyKey key, HotkeyModifiers modifiers)
        {
            var service = CreateService();

            Assert.Equal(
                HotkeyCapability.Unsupported,
                service.GetCapability(new HotkeyGesture(key, modifiers)));
        }

        /// <summary>
        /// Registration is refused with Unsupported, and the result echoes the
        /// exact gesture that was attempted.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_ReturnsUnsupported_PreservingGesture()
        {
            var service = CreateService();

            var result = await service.RegisterAsync(Gesture, CancellationToken.None);

            Assert.Equal(HotkeyRegistrationStatus.Unsupported, result.Status);
            Assert.Equal(Gesture, result.Gesture);
        }

        /// <summary>
        /// A pre-cancelled token must not turn registration into an
        /// OperationCanceledException: the fallback answers Unsupported
        /// regardless of cancellation state.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_PreCancelledToken_ReturnsUnsupported_NotThrowing()
        {
            var service = CreateService();

            var result = await service.RegisterAsync(
                Gesture,
                new CancellationToken(canceled: true));

            Assert.Equal(HotkeyRegistrationStatus.Unsupported, result.Status);
            Assert.Equal(Gesture, result.Gesture);
        }

        /// <summary>
        /// Unregistration is a no-op that always completes.
        /// </summary>
        [Fact]
        public async Task UnregisterAsync_Completes_ForNormalToken()
        {
            var service = CreateService();

            await service.UnregisterAsync(Gesture, CancellationToken.None);
        }

        /// <summary>
        /// Unregistration must not observe cancellation either.
        /// </summary>
        [Fact]
        public async Task UnregisterAsync_Completes_ForPreCancelledToken()
        {
            var service = CreateService();

            await service.UnregisterAsync(Gesture, new CancellationToken(canceled: true));
        }

        /// <summary>
        /// Even after a registration attempt the event never fires: there is
        /// no OS-level hook that could observe a press.
        /// </summary>
        [Fact]
        public async Task HotkeyPressed_NeverRaises()
        {
            var service = CreateService();
            var raised = false;
            service.HotkeyPressed += (_, _) => raised = true;

            await service.RegisterAsync(Gesture, CancellationToken.None);
            await Task.Delay(100);

            Assert.False(raised);
        }

        /// <summary>
        /// Dispose is idempotent and the service stays fully queryable and
        /// registrable afterwards.
        /// </summary>
        [Fact]
        public async Task Dispose_Twice_IsSafe_AndServiceRemainsUsable()
        {
            var service = CreateService();

            service.Dispose();
            service.Dispose();

            Assert.Equal(
                HotkeyCapability.Unsupported,
                service.GetCapability(Gesture));

            var result = await service.RegisterAsync(Gesture, CancellationToken.None);
            Assert.Equal(HotkeyRegistrationStatus.Unsupported, result.Status);
            await service.UnregisterAsync(Gesture, CancellationToken.None);
        }
    }
}
