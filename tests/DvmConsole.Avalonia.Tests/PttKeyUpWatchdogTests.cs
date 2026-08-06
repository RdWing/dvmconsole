// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the internal PTT hotkey down-latch and
    /// key-up watchdog. The shell drives the internal tick; public PTT API
    /// shape remains unchanged.
    /// </summary>
    public sealed class PttKeyUpWatchdogTests
    {
        private static readonly HotkeyGesture Gesture =
            new(HotkeyKey.F9, HotkeyModifiers.Control);

        [Fact]
        public void InternalWatchdogSurface_IsNotPublic_AndHasExactShape()
        {
            var type = typeof(PttCapabilityViewModel);
            var method = type.GetMethod(
                "WatchdogTick",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var signal = type.GetEvent(
                "KeyUpMissed",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(method);
            Assert.False(method!.IsPublic);
            Assert.Equal(typeof(void), method.ReturnType);
            Assert.Equal(new[] { typeof(bool) },
                Array.ConvertAll(method.GetParameters(), p => p.ParameterType));
            Assert.NotNull(signal);
            Assert.Equal(typeof(Action), signal!.EventHandlerType);
            Assert.False(signal.AddMethod!.IsPublic);
            Assert.False(signal.RemoveMethod!.IsPublic);
        }

        [Fact]
        public void Momentary_PressedLatches_RepeatPressedIsIgnored_UntilReleased()
        {
            var (vm, slot) = Create();
            var requests = TrackRequests(vm);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);

            Assert.True(vm.IsEngaged);
            Assert.True(slot.PttEngaged);
            Assert.Equal(new[] { true }, requests);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Released);
            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Released);

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Equal(new[] { true, false }, requests);
        }

        [Fact]
        public void Momentary_WatchdogWithPhysicalKeyUp_ForcesReleaseAndSignalsOnce()
        {
            var (vm, slot) = Create();
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Equal(new[] { true, false }, requests);
            Assert.Single(missed);
        }

        [Fact]
        public void Momentary_WatchdogWhilePhysicalKeyDown_IsNoOp()
        {
            var (vm, slot) = Create();
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: true);

            Assert.True(vm.IsEngaged);
            Assert.True(slot.PttEngaged);
            Assert.Equal(new[] { true }, requests);
            Assert.Empty(missed);
        }

        [Fact]
        public void WatchdogWithLatchClear_IsNoOp()
        {
            var (vm, slot) = Create();
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Empty(requests);
            Assert.Empty(missed);
        }

        [Fact]
        public void Toggle_RepeatPressedIsIgnored_ReleaseAndWatchdogOnlyClearLatch()
        {
            var (vm, slot) = Create(toggleMode: true);
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);
            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Released);
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);

            Assert.True(vm.IsEngaged);
            Assert.True(slot.PttEngaged);
            Assert.Equal(new[] { true }, requests);
            Assert.Empty(missed);

            vm.ApplyHotkeyPress(Gesture, HotkeyEventType.Pressed);

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Equal(new[] { true, false }, requests);
        }

        [Fact]
        public void MismatchedGesture_DoesNotArmLatchOrTriggerWatchdog()
        {
            var (vm, slot) = Create();
            var wrong = new HotkeyGesture(HotkeyKey.F10, HotkeyModifiers.Control);
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            vm.ApplyHotkeyPress(wrong, HotkeyEventType.Pressed);
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Empty(requests);
            Assert.Empty(missed);
        }

        [Fact]
        public void PointerPath_DoesNotUseHotkeyLatchOrWatchdog()
        {
            var (vm, slot) = Create();
            var requests = TrackRequests(vm);
            var missed = TrackMissedKeyUps(vm);

            vm.PttPointerDown();
            InvokeWatchdogTick(vm, keyIsPhysicallyDown: false);

            Assert.True(vm.IsEngaged);
            Assert.True(slot.PttEngaged);
            Assert.Equal(new[] { true }, requests);
            Assert.Empty(missed);

            vm.PttPointerUp();

            Assert.False(vm.IsEngaged);
            Assert.False(slot.PttEngaged);
            Assert.Equal(new[] { true, false }, requests);
        }

        private static (PttCapabilityViewModel Vm, ChannelSlotViewModel Slot) Create(
            bool toggleMode = false)
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var vm = new PttCapabilityViewModel(
                new FakeGlobalHotkeyService(),
                () => slot,
                () => new[] { slot });
            vm.SetHotkey(Gesture);
            vm.ToggleMode = toggleMode;
            return (vm, slot);
        }

        private static List<bool> TrackRequests(PttCapabilityViewModel vm)
        {
            var requests = new List<bool>();
            vm.PttStateRequested += engaged => requests.Add(engaged);
            return requests;
        }

        private static List<int> TrackMissedKeyUps(PttCapabilityViewModel vm)
        {
            var missed = new List<int>();
            var signal = typeof(PttCapabilityViewModel).GetEvent(
                "KeyUpMissed",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(signal);
            signal!.GetAddMethod(nonPublic: true)!.Invoke(
                vm,
                new object?[] { new Action(() => missed.Add(1)) });
            return missed;
        }

        private static void InvokeWatchdogTick(
            PttCapabilityViewModel vm,
            bool keyIsPhysicallyDown)
        {
            var method = typeof(PttCapabilityViewModel).GetMethod(
                "WatchdogTick",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(method);
            method!.Invoke(vm, new object[] { keyIsPhysicallyDown });
        }

        private sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
        {
#pragma warning disable CS0067 // The contract requires the event; this fake never raises it.
            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
#pragma warning restore CS0067

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
                => HotkeyCapability.Unsupported;

            public Task<HotkeyRegistrationResult> RegisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
                => Task.FromResult(new HotkeyRegistrationResult(
                    HotkeyRegistrationStatus.Registered,
                    gesture));

            public Task UnregisterAsync(
                HotkeyGesture gesture,
                CancellationToken cancellationToken)
                => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }
}
