// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// Small, explicit P/Invoke surface for the Apple CoreAudio HAL and
    /// AudioQueue Services APIs used by the macOS audio adapters.
    /// </summary>
    internal static class CoreAudioNative
    {
        internal const string CoreAudioLibrary = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        internal const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        internal const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        internal const uint SystemObject = 1;
        internal const uint ScopeGlobal = 0x676C6F62; // 'glob'
        internal const uint ScopeInput = 0x696E7074; // 'inpt'
        internal const uint ScopeOutput = 0x6F757470; // 'outp'
        internal const uint ElementMaster = 0;

        internal const uint PropertyDevices = 0x64657623; // 'dev#'
        internal const uint PropertyDefaultInputDevice = 0x64496E20; // 'dIn '
        internal const uint PropertyDefaultOutputDevice = 0x644F7574; // 'dOut'
        internal const uint PropertyObjectName = 0x6C6E616D; // 'lnam'
        internal const uint PropertyDeviceUid = 0x75696420; // 'uid '
        internal const uint PropertyStreamConfiguration = 0x736C6179; // 'slay'
        internal const uint PropertyDeviceIsAlive = 0x6C69766E; // 'livn'

        internal const uint AudioQueuePropertyCurrentDevice = 0x61716364; // 'aqcd'
        internal const uint AudioQueueParameterVolume = 1;
        internal const uint AudioFormatLinearPcm = 0x6C70636D; // 'lpcm'
        internal const uint AudioFormatFlagIsSignedInteger = 1u << 2;
        internal const uint AudioFormatFlagIsPacked = 1u << 3;

        internal const int AudioHardwareNoError = 0;
        internal const int AudioDevicePermissionsError = 0x21686F67; // '!hog'
        internal const int AudioQueueErrorInvalidDevice = -66680;
        internal const int AudioQueueErrorPermissions = -66676;

        private const uint CfStringEncodingUtf8 = 0x08000100;

        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioObjectPropertyAddress
        {
            internal uint Selector;
            internal uint Scope;
            internal uint Element;

            internal AudioObjectPropertyAddress(uint selector, uint scope, uint element)
            {
                Selector = selector;
                Scope = scope;
                Element = element;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioStreamBasicDescription
        {
            internal double SampleRate;
            internal uint FormatId;
            internal uint FormatFlags;
            internal uint BytesPerPacket;
            internal uint FramesPerPacket;
            internal uint BytesPerFrame;
            internal uint ChannelsPerFrame;
            internal uint BitsPerChannel;
            internal uint Reserved1;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int AudioObjectPropertyListenerProc(
            uint objectId,
            uint numberAddresses,
            IntPtr addresses,
            IntPtr clientData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void AudioQueueInputCallback(
            IntPtr userData,
            IntPtr queue,
            IntPtr buffer,
            IntPtr startTime,
            uint numberPacketDescriptions,
            IntPtr packetDescriptions);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void AudioQueueOutputCallback(
            IntPtr userData,
            IntPtr queue,
            IntPtr buffer);

        [DllImport(CoreAudioLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectGetPropertyDataSize(
            uint objectId,
            ref AudioObjectPropertyAddress address,
            uint qualifierDataSize,
            IntPtr qualifierData,
            out uint dataSize);

        [DllImport(CoreAudioLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectGetPropertyData(
            uint objectId,
            ref AudioObjectPropertyAddress address,
            uint qualifierDataSize,
            IntPtr qualifierData,
            ref uint dataSize,
            IntPtr data);

        [DllImport(CoreAudioLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectAddPropertyListener(
            uint objectId,
            ref AudioObjectPropertyAddress address,
            AudioObjectPropertyListenerProc listener,
            IntPtr clientData);

        [DllImport(CoreAudioLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioObjectRemovePropertyListener(
            uint objectId,
            ref AudioObjectPropertyAddress address,
            AudioObjectPropertyListenerProc listener,
            IntPtr clientData);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueNewInput(
            ref AudioStreamBasicDescription format,
            AudioQueueInputCallback callback,
            IntPtr userData,
            IntPtr runLoop,
            IntPtr runLoopMode,
            uint flags,
            out IntPtr queue);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueNewOutput(
            ref AudioStreamBasicDescription format,
            AudioQueueOutputCallback callback,
            IntPtr userData,
            IntPtr runLoop,
            IntPtr runLoopMode,
            uint flags,
            out IntPtr queue);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueAllocateBuffer(
            IntPtr queue,
            uint byteSize,
            out IntPtr buffer);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueEnqueueBuffer(
            IntPtr queue,
            IntPtr buffer,
            uint packetDescriptionCount,
            IntPtr packetDescriptions);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueStop(
            IntPtr queue,
            [MarshalAs(UnmanagedType.I1)] bool immediate);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueDispose(
            IntPtr queue,
            [MarshalAs(UnmanagedType.I1)] bool immediate);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueSetProperty(
            IntPtr queue,
            uint propertyId,
            IntPtr data,
            uint dataSize);

        [DllImport(AudioToolboxLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AudioQueueSetParameter(
            IntPtr queue,
            uint parameterId,
            float value);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator,
            IntPtr cString,
            uint encoding);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CFStringGetCStringPtr(
            IntPtr stringRef,
            uint encoding);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFStringGetCString(
            IntPtr stringRef,
            IntPtr buffer,
            nint bufferSize,
            uint encoding);

        [DllImport(CoreFoundationLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void CFRelease(IntPtr cfObject);

        internal static AudioStreamBasicDescription CreatePcmDescription(PcmFormat format)
        {
            if (format.BitsPerSample != 16 || format.Channels <= 0 || format.SampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(format),
                    format,
                    "The macOS audio adapter currently supports positive-rate 16-bit PCM only.");
            }

            var bytesPerFrame = checked(format.BytesPerSample * format.Channels);
            return new AudioStreamBasicDescription
            {
                SampleRate = format.SampleRate,
                FormatId = AudioFormatLinearPcm,
                FormatFlags = AudioFormatFlagIsSignedInteger | AudioFormatFlagIsPacked,
                BytesPerPacket = (uint)bytesPerFrame,
                FramesPerPacket = 1,
                BytesPerFrame = (uint)bytesPerFrame,
                ChannelsPerFrame = (uint)format.Channels,
                BitsPerChannel = (uint)format.BitsPerSample,
            };
        }

        internal static uint[] GetDeviceIds()
        {
            var address = new AudioObjectPropertyAddress(PropertyDevices, ScopeGlobal, ElementMaster);
            var status = AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var dataSize);
            ThrowIfError(status, AudioDeviceErrorKind.DeviceUnavailable, "CoreAudio device enumeration size query failed");

            if (dataSize == 0)
            {
                return Array.Empty<uint>();
            }

            var buffer = Marshal.AllocHGlobal(checked((int)dataSize));
            try
            {
                var ioSize = dataSize;
                status = AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref ioSize, buffer);
                ThrowIfError(status, AudioDeviceErrorKind.DeviceUnavailable, "CoreAudio device enumeration failed");

                var count = (int)(ioSize / sizeof(uint));
                var ids = new uint[count];
                for (var i = 0; i < count; i++)
                {
                    ids[i] = unchecked((uint)Marshal.ReadInt32(buffer, i * sizeof(uint)));
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static uint GetDefaultDevice(AudioDeviceDirection direction)
        {
            var selector = direction == AudioDeviceDirection.Input
                ? PropertyDefaultInputDevice
                : PropertyDefaultOutputDevice;
            return GetUInt32(SystemObject, new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMaster));
        }

        internal static string? GetDeviceString(uint deviceId, uint selector)
        {
            var address = new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMaster);
            var storage = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                var dataSize = (uint)IntPtr.Size;
                var status = AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref dataSize, storage);
                if (status != AudioHardwareNoError)
                {
                    return null;
                }

                var stringRef = Marshal.ReadIntPtr(storage);
                if (stringRef == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return ReadCfString(stringRef);
                }
                finally
                {
                    CFRelease(stringRef);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }

        internal static int GetChannelCount(uint deviceId, AudioDeviceDirection direction)
        {
            var scope = direction == AudioDeviceDirection.Input ? ScopeInput : ScopeOutput;
            var address = new AudioObjectPropertyAddress(PropertyStreamConfiguration, scope, ElementMaster);
            var status = AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var dataSize);
            if (status != AudioHardwareNoError || dataSize < sizeof(uint))
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)dataSize));
            try
            {
                var ioSize = dataSize;
                status = AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref ioSize, buffer);
                if (status != AudioHardwareNoError)
                {
                    return 0;
                }

                var numberBuffers = Marshal.ReadInt32(buffer);
                var stride = sizeof(uint) + sizeof(uint) + IntPtr.Size;
                var firstBufferOffset = IntPtr.Size == 8 ? 8 : sizeof(uint);
                var channels = 0;
                for (var i = 0; i < numberBuffers; i++)
                {
                    var offset = firstBufferOffset + i * stride;
                    if (offset + sizeof(uint) > ioSize)
                    {
                        break;
                    }

                    channels = checked(channels + Marshal.ReadInt32(buffer, offset));
                }

                return channels;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static int AddDeviceAliveListener(
            uint deviceId,
            AudioObjectPropertyListenerProc listener,
            IntPtr clientData)
        {
            var address = new AudioObjectPropertyAddress(PropertyDeviceIsAlive, ScopeGlobal, ElementMaster);
            return AudioObjectAddPropertyListener(deviceId, ref address, listener, clientData);
        }

        internal static int RemoveDeviceAliveListener(
            uint deviceId,
            AudioObjectPropertyListenerProc listener,
            IntPtr clientData)
        {
            var address = new AudioObjectPropertyAddress(PropertyDeviceIsAlive, ScopeGlobal, ElementMaster);
            return AudioObjectRemovePropertyListener(deviceId, ref address, listener, clientData);
        }

        internal static IntPtr CreateCfString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var native = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, native, bytes.Length);
                var result = CFStringCreateWithCString(IntPtr.Zero, native, CfStringEncodingUtf8);
                if (result == IntPtr.Zero)
                {
                    throw new AudioDeviceException(AudioDeviceErrorKind.OpenFailed, "CoreFoundation could not create a device UID string.");
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        internal static void ReleaseCfString(IntPtr stringRef)
        {
            if (stringRef != IntPtr.Zero)
            {
                CFRelease(stringRef);
            }
        }

        internal static IntPtr GetAudioQueueBufferData(IntPtr buffer)
            => Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);

        internal static uint GetAudioQueueBufferCapacity(IntPtr buffer)
            => unchecked((uint)Marshal.ReadInt32(buffer, 0));

        internal static uint GetAudioQueueBufferByteSize(IntPtr buffer)
            => unchecked((uint)Marshal.ReadInt32(buffer, IntPtr.Size == 8 ? 16 : 8));

        internal static void SetAudioQueueBufferByteSize(IntPtr buffer, uint byteSize)
            => Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, checked((int)byteSize));

        internal static void SetQueueCurrentDevice(IntPtr queue, string uid)
        {
            var stringRef = CreateCfString(uid);
            var storage = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(storage, stringRef);
                var status = AudioQueueSetProperty(
                    queue,
                    AudioQueuePropertyCurrentDevice,
                    storage,
                    (uint)IntPtr.Size);
                ThrowIfError(status, AudioDeviceErrorKind.OpenFailed, "AudioQueue could not select the requested CoreAudio device");
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
                ReleaseCfString(stringRef);
            }
        }

        internal static void ThrowIfError(int status, AudioDeviceErrorKind kind, string operation)
        {
            if (status == AudioHardwareNoError)
            {
                return;
            }

            var permission = status == AudioDevicePermissionsError || status == AudioQueueErrorPermissions;
            var suffix = permission
                ? " Microphone permission may be required in System Settings > Privacy & Security."
                : $" (OSStatus {status}).";
            throw new AudioDeviceException(
                permission ? AudioDeviceErrorKind.OpenFailed : kind,
                operation + suffix);
        }

        private static uint GetUInt32(uint objectId, AudioObjectPropertyAddress address)
        {
            var storage = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                var dataSize = (uint)sizeof(uint);
                var status = AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref dataSize, storage);
                ThrowIfError(status, AudioDeviceErrorKind.DeviceUnavailable, "CoreAudio property query failed");
                return unchecked((uint)Marshal.ReadInt32(storage));
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }

        private static string? ReadCfString(IntPtr stringRef)
        {
            var direct = CFStringGetCStringPtr(stringRef, CfStringEncodingUtf8);
            if (direct != IntPtr.Zero)
            {
                return Marshal.PtrToStringUTF8(direct);
            }

            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                return CFStringGetCString(stringRef, buffer, 4096, CfStringEncodingUtf8)
                    ? Marshal.PtrToStringUTF8(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
