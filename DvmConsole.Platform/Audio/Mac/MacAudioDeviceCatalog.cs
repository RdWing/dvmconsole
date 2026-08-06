// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// CoreAudio HAL catalog. The catalog re-queries the HAL on every public
    /// enumeration so device IDs remain valid after hot-plug or default-device
    /// changes; the HAL listener provides an optional change notification for
    /// callers that want to refresh their UI immediately.
    /// </summary>
    public sealed class MacAudioDeviceCatalog : IAudioDeviceCatalog
    {
        private static readonly CoreAudioNative.AudioObjectPropertyListenerProc SystemListener = OnSystemPropertyChanged;

        /// <summary>
        /// System-object properties whose changes invalidate the catalog snapshot:
        /// the device list and both default-device selectors.
        /// </summary>
        private static readonly (uint Selector, string Name)[] SystemProperties =
        {
            (CoreAudioNative.PropertyDevices, "device-list"),
            (CoreAudioNative.PropertyDefaultInputDevice, "default-input-device"),
            (CoreAudioNative.PropertyDefaultOutputDevice, "default-output-device"),
        };

        private readonly object _refreshGate = new();
        private readonly IntPtr _listenerData;
        private int _disposed;
        private int _notificationPending;

        public MacAudioDeviceCatalog()
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("The CoreAudio catalog is available only on macOS.");
            }

            var handle = GCHandle.Alloc(this);
            _listenerData = GCHandle.ToIntPtr(handle);
            try
            {
                foreach (var (selector, name) in SystemProperties)
                {
                    var address = new CoreAudioNative.AudioObjectPropertyAddress(
                        selector,
                        CoreAudioNative.ScopeGlobal,
                        CoreAudioNative.ElementMaster);
                    var status = CoreAudioNative.AudioObjectAddPropertyListener(
                        CoreAudioNative.SystemObject,
                        ref address,
                        SystemListener,
                        _listenerData);
                    if (status != CoreAudioNative.AudioHardwareNoError)
                    {
                        throw new AudioDeviceException(
                            AudioDeviceErrorKind.DeviceUnavailable,
                            $"CoreAudio could not register the {name} listener (OSStatus {status}).");
                    }
                }
            }
            catch
            {
                // Registration failed part-way: drop the listeners already
                // registered, release the handle, and surface the failure.
                RemoveSystemListeners();
                handle.Free();
                throw;
            }
        }

        /// <summary>
        /// Raised after CoreAudio reports a device-list or default-device change.
        /// The event is dispatched away from the HAL notification callback.
        /// </summary>
        public event EventHandler? DevicesChanged;

        public IReadOnlyList<AudioDeviceInfo> GetInputs()
            => Refresh().Inputs.Select(descriptor => descriptor.Info).ToArray();

        public IReadOnlyList<AudioDeviceInfo> GetOutputs()
            => Refresh().Outputs.Select(descriptor => descriptor.Info).ToArray();

        public AudioDeviceInfo? GetDefaultInput()
            => FindDefault(AudioDeviceDirection.Input)?.Info;

        public AudioDeviceInfo? GetDefaultOutput()
            => FindDefault(AudioDeviceDirection.Output)?.Info;

        public bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device)
        {
            if (id.IsDefault)
            {
                device = GetDefaultOutput() ?? GetDefaultInput();
                return device is not null;
            }

            var snapshot = Refresh();
            var descriptor = snapshot.Inputs.Concat(snapshot.Outputs)
                .FirstOrDefault(candidate => MacAudioDeviceKey.Matches(candidate.Info.Id.Value, id.Value));
            device = descriptor?.Info;
            return descriptor is not null;
        }

        internal bool TryResolve(
            AudioDeviceDirection direction,
            AudioDeviceId id,
            out MacAudioDeviceDescriptor? descriptor)
        {
            descriptor = id.IsDefault
                ? FindDefault(direction)
                : Refresh(direction).FirstOrDefault(candidate =>
                    MacAudioDeviceKey.Matches(candidate.Info.Id.Value, id.Value));
            return descriptor is not null;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            RemoveSystemListeners();

            if (_listenerData != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(_listenerData).Free();
            }

            return ValueTask.CompletedTask;
        }

        private void RemoveSystemListeners()
        {
            foreach (var (selector, _) in SystemProperties)
            {
                var address = new CoreAudioNative.AudioObjectPropertyAddress(
                    selector,
                    CoreAudioNative.ScopeGlobal,
                    CoreAudioNative.ElementMaster);
                _ = CoreAudioNative.AudioObjectRemovePropertyListener(
                    CoreAudioNative.SystemObject,
                    ref address,
                    SystemListener,
                    _listenerData);
            }
        }

        private DeviceSnapshot Refresh()
        {
            lock (_refreshGate)
            {
                var inputs = new List<MacAudioDeviceDescriptor>();
                var outputs = new List<MacAudioDeviceDescriptor>();
                foreach (var nativeId in CoreAudioNative.GetDeviceIds())
                {
                    AddDescriptor(nativeId, AudioDeviceDirection.Input, inputs);
                    AddDescriptor(nativeId, AudioDeviceDirection.Output, outputs);
                }

                return new DeviceSnapshot(inputs, outputs);
            }
        }

        private IReadOnlyList<MacAudioDeviceDescriptor> Refresh(AudioDeviceDirection direction)
        {
            lock (_refreshGate)
            {
                var result = new List<MacAudioDeviceDescriptor>();
                foreach (var nativeId in CoreAudioNative.GetDeviceIds())
                {
                    AddDescriptor(nativeId, direction, result);
                }

                return result;
            }
        }

        private MacAudioDeviceDescriptor? FindDefault(AudioDeviceDirection direction)
        {
            var defaultNativeId = CoreAudioNative.GetDefaultDevice(direction);
            if (defaultNativeId == 0)
            {
                return null;
            }

            return Refresh(direction).FirstOrDefault(candidate => candidate.NativeId == defaultNativeId);
        }

        private static void AddDescriptor(
            uint nativeId,
            AudioDeviceDirection direction,
            ICollection<MacAudioDeviceDescriptor> destination)
        {
            try
            {
                var channels = CoreAudioNative.GetChannelCount(nativeId, direction);
                if (channels <= 0)
                {
                    return;
                }

                var name = CoreAudioNative.GetDeviceString(nativeId, CoreAudioNative.PropertyObjectName);
                var uid = CoreAudioNative.GetDeviceString(nativeId, CoreAudioNative.PropertyDeviceUid);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uid))
                {
                    return;
                }

                var key = MacAudioDeviceKey.ToDeviceId(direction, uid, name, channels);
                destination.Add(new MacAudioDeviceDescriptor(
                    nativeId,
                    uid,
                    channels,
                    new AudioDeviceInfo(key, direction, name)));
            }
            catch (AudioDeviceException)
            {
                // A device can disappear between the device-list query and its
                // individual property queries. Treat that race as a stale entry.
            }
            catch (ExternalException)
            {
                // The same race can surface as a marshaling/native exception on
                // a disappearing aggregate device.
            }
        }

        private static int OnSystemPropertyChanged(
            uint objectId,
            uint numberAddresses,
            IntPtr addresses,
            IntPtr clientData)
        {
            if (clientData == IntPtr.Zero)
            {
                return CoreAudioNative.AudioHardwareNoError;
            }

            try
            {
                var catalog = (MacAudioDeviceCatalog?)GCHandle.FromIntPtr(clientData).Target;
                catalog?.QueueDevicesChanged();
            }
            catch (InvalidOperationException)
            {
                // Disposal may race with the final HAL callback.
            }

            return CoreAudioNative.AudioHardwareNoError;
        }

        private void QueueDevicesChanged()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _notificationPending, 1) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var catalog = (MacAudioDeviceCatalog)state!;
                    Volatile.Write(ref catalog._notificationPending, 0);
                    if (Volatile.Read(ref catalog._disposed) == 0)
                    {
                        catalog.DevicesChanged?.Invoke(catalog, EventArgs.Empty);
                    }
                },
                this);
        }

        private sealed record DeviceSnapshot(
            IReadOnlyList<MacAudioDeviceDescriptor> Inputs,
            IReadOnlyList<MacAudioDeviceDescriptor> Outputs);
    }

    internal sealed record MacAudioDeviceDescriptor(
        uint NativeId,
        string Uid,
        int Channels,
        AudioDeviceInfo Info);
}
