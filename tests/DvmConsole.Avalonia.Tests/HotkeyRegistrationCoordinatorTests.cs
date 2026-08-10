// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Hotkeys;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the hotkey registration coordinator: the
    /// missing seam that actually registers and unregisters the
    /// configured PTT hotkey gesture on the platform service.
    /// </summary>
    public sealed class HotkeyRegistrationCoordinatorTests
    {
        private sealed class RecordingHotkeyService : IGlobalHotkeyService
        {
            public readonly List<string> Calls = new();
            public bool RegisterResultIsAlreadyRegistered;
            public int DisposeCount { get; private set; }

            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
                => HotkeyCapability.Available;

            public Task<HotkeyRegistrationResult> RegisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
            {
                Calls.Add($"REGISTER:{gesture.Key}, {gesture.Modifiers}");
                return Task.FromResult(new HotkeyRegistrationResult(
                    RegisterResultIsAlreadyRegistered
                        ? HotkeyRegistrationStatus.AlreadyRegistered
                        : HotkeyRegistrationStatus.Registered,
                    gesture));
            }

            public Task UnregisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
            {
                Calls.Add($"UNREGISTER:{gesture.Key}, {gesture.Modifiers}");
                return Task.CompletedTask;
            }

            public void Dispose() => DisposeCount++;
        }

        private static readonly HotkeyGesture GestureF9 = new(
            HotkeyKey.F9, HotkeyModifiers.Control);

        private static readonly HotkeyGesture GestureF10 = new(
            HotkeyKey.F10, HotkeyModifiers.Alt);

        private static PttCapabilityViewModel CreatePtt(IGlobalHotkeyService service)
            => new(service, () => null, () => Array.Empty<ChannelSlotViewModel>());

        [Fact]
        public async Task Coordinator_AttachWithNoHotkey_RegistersNothing()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            await WaitForAsync(() => coordinator.Idle);
            Assert.Empty(service.Calls);

            coordinator.Dispose();
            Assert.Empty(service.Calls);
        }

        [Fact]
        public async Task Coordinator_ApplyGesture_RegistersOnce()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(new[] { "REGISTER:F9, Control" }, service.Calls);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);
            Assert.Equal(new[] { "REGISTER:F9, Control" }, service.Calls);

            coordinator.Dispose();
        }

        [Fact]
        public async Task Coordinator_ChangeGesture_UnregistersOldThenRegistersNew()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);

            ptt.SetHotkey(GestureF10);
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(
                new[]
                {
                    "REGISTER:F9, Control",
                    "UNREGISTER:F9, Control",
                    "REGISTER:F10, Alt",
                },
                service.Calls);

            coordinator.Dispose();
        }

        [Fact]
        public async Task Coordinator_ClearHotkey_Unregisters()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);

            ptt.ClearHotkey();
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(
                new[]
                {
                    "REGISTER:F9, Control",
                    "UNREGISTER:F9, Control",
                },
                service.Calls);

            coordinator.Dispose();
        }

        [Fact]
        public async Task Coordinator_AttachWithConfiguredHotkey_RegistersExistingGesture()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            ptt.SetHotkey(GestureF9);

            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(new[] { "REGISTER:F9, Control" }, service.Calls);
            coordinator.Dispose();
        }

        [Fact]
        public async Task Coordinator_Dispose_UnregistersAndDetaches()
        {
            var service = new RecordingHotkeyService();
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);

            coordinator.Dispose();
            Assert.Equal(
                new[]
                {
                    "REGISTER:F9, Control",
                    "UNREGISTER:F9, Control",
                },
                service.Calls);

            // Detached: further PTT changes reach no one and the service
            // is not touched again.
            ptt.SetHotkey(GestureF10);
            await WaitForAsync(() => coordinator.Idle);
            ptt.ClearHotkey();
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(
                new[]
                {
                    "REGISTER:F9, Control",
                    "UNREGISTER:F9, Control",
                },
                service.Calls);
            Assert.Equal(0, service.DisposeCount);
        }

        [Fact]
        public async Task Coordinator_RegistrationFailure_DoesNotRetryOrThrow()
        {
            var service = new RecordingHotkeyService
            {
                RegisterResultIsAlreadyRegistered = true,
            };
            var ptt = CreatePtt(service);
            var coordinator = new HotkeyRegistrationCoordinator(service, ptt);

            ptt.SetHotkey(GestureF9);
            await WaitForAsync(() => coordinator.Idle);

            Assert.Equal(new[] { "REGISTER:F9, Control" }, service.Calls);
            coordinator.Dispose();
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(condition());
        }
    }
}
