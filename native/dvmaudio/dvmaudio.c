#include "dvmaudio.h"

#include <AudioToolbox/AudioToolbox.h>
#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define DVM_AUDIO_RING_SECONDS 2

struct DvmAudioStream {
    AudioUnit unit;
    int32_t input;
    uint32_t sample_rate;
    uint32_t ring_capacity;
    int16_t *ring;
    _Atomic uint32_t read_index;
    _Atomic uint32_t write_index;
    _Atomic int32_t running;
};

static int32_t stream_channels(AudioDeviceID device, AudioObjectPropertyScope scope)
{
    AudioObjectPropertyAddress address = {
        kAudioDevicePropertyStreamConfiguration,
        scope,
        kAudioObjectPropertyElementMain};
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(device, &address, 0, NULL, &size) != noErr || size < sizeof(AudioBufferList))
        return 0;

    AudioBufferList *buffers = (AudioBufferList *)calloc(1, size);
    if (buffers == NULL)
        return 0;

    UInt32 actual_size = size;
    OSStatus status = AudioObjectGetPropertyData(device, &address, 0, NULL, &actual_size, buffers);
    int32_t channels = 0;
    if (status == noErr) {
        for (UInt32 index = 0; index < buffers->mNumberBuffers; index++)
            channels += (int32_t)buffers->mBuffers[index].mNumberChannels;
    }

    free(buffers);
    return channels;
}

static int32_t has_direction(AudioDeviceID device, int32_t input)
{
    return stream_channels(
        device,
        input ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput) > 0;
}

static AudioDeviceID default_device(int32_t input)
{
    AudioObjectPropertyAddress address = {
        input ? kAudioHardwarePropertyDefaultInputDevice : kAudioHardwarePropertyDefaultOutputDevice,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    AudioDeviceID device = kAudioObjectUnknown;
    UInt32 size = sizeof(device);
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, &device) != noErr)
        return kAudioObjectUnknown;
    return device;
}

int32_t dvm_audio_get_device_count(int32_t input, int32_t *count)
{
    if (count == NULL)
        return -1;

    AudioObjectPropertyAddress address = {
        kAudioHardwarePropertyDevices,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    UInt32 size = 0;
    OSStatus status = AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, NULL, &size);
    if (status != noErr)
        return (int32_t)status;

    AudioDeviceID *devices = (AudioDeviceID *)malloc(size);
    if (devices == NULL)
        return -2;

    status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, devices);
    if (status != noErr) {
        free(devices);
        return (int32_t)status;
    }

    int32_t matches = 0;
    UInt32 device_count = size / sizeof(AudioDeviceID);
    for (UInt32 index = 0; index < device_count; index++)
        if (has_direction(devices[index], input))
            matches++;

    free(devices);
    *count = matches;
    return 0;
}

static int32_t copy_device_name(AudioDeviceID device, char *name, uint32_t capacity)
{
    if (name == NULL || capacity == 0)
        return -1;

    AudioObjectPropertyAddress address = {
        kAudioObjectPropertyName,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    CFStringRef device_name = NULL;
    UInt32 size = sizeof(device_name);
    OSStatus status = AudioObjectGetPropertyData(device, &address, 0, NULL, &size, &device_name);
    if (status != noErr || device_name == NULL) {
        name[0] = '\0';
        return status == noErr ? -2 : (int32_t)status;
    }

    Boolean copied = CFStringGetCString(device_name, name, capacity, kCFStringEncodingUTF8);
    CFRelease(device_name);
    if (!copied)
        name[0] = '\0';
    return copied ? 0 : -3;
}

int32_t dvm_audio_get_device(
    int32_t input,
    int32_t index,
    uint64_t *device_id,
    char *name,
    uint32_t name_capacity,
    int32_t *is_default)
{
    if (index < 0 || device_id == NULL || is_default == NULL)
        return -1;

    AudioObjectPropertyAddress address = {
        kAudioHardwarePropertyDevices,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    UInt32 size = 0;
    OSStatus status = AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, NULL, &size);
    if (status != noErr)
        return (int32_t)status;

    AudioDeviceID *devices = (AudioDeviceID *)malloc(size);
    if (devices == NULL)
        return -2;

    status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, devices);
    if (status != noErr) {
        free(devices);
        return (int32_t)status;
    }

    int32_t match = -1;
    UInt32 device_count = size / sizeof(AudioDeviceID);
    for (UInt32 device_index = 0; device_index < device_count; device_index++) {
        if (!has_direction(devices[device_index], input))
            continue;
        match++;
        if (match != index)
            continue;

        *device_id = devices[device_index];
        *is_default = devices[device_index] == default_device(input);
        int32_t name_status = copy_device_name(devices[device_index], name, name_capacity);
        free(devices);
        return name_status;
    }

    free(devices);
    return -4;
}

static void ring_push(DvmAudioStream *stream, const int16_t *samples, uint32_t count)
{
    for (uint32_t index = 0; index < count; index++) {
        uint32_t write = atomic_load_explicit(&stream->write_index, memory_order_relaxed);
        uint32_t next = (write + 1) % stream->ring_capacity;
        uint32_t read = atomic_load_explicit(&stream->read_index, memory_order_acquire);
        if (next == read) {
            read = (read + 1) % stream->ring_capacity;
            atomic_store_explicit(&stream->read_index, read, memory_order_release);
        }
        stream->ring[write] = samples[index];
        atomic_store_explicit(&stream->write_index, next, memory_order_release);
    }
}

static uint32_t ring_pop(DvmAudioStream *stream, int16_t *samples, uint32_t capacity)
{
    uint32_t count = 0;
    while (count < capacity) {
        uint32_t read = atomic_load_explicit(&stream->read_index, memory_order_relaxed);
        uint32_t write = atomic_load_explicit(&stream->write_index, memory_order_acquire);
        if (read == write)
            break;
        samples[count++] = stream->ring[read];
        atomic_store_explicit(&stream->read_index, (read + 1) % stream->ring_capacity, memory_order_release);
    }
    return count;
}

static OSStatus input_callback(
    void *ref_con,
    AudioUnitRenderActionFlags *action_flags,
    const AudioTimeStamp *timestamp,
    UInt32 bus_number,
    UInt32 number_frames,
    AudioBufferList *data)
{
    (void)action_flags;
    (void)timestamp;
    (void)data;
    DvmAudioStream *stream = (DvmAudioStream *)ref_con;
    if (stream == NULL || !atomic_load_explicit(&stream->running, memory_order_acquire))
        return noErr;

    int16_t *samples = (int16_t *)malloc(number_frames * sizeof(int16_t));
    if (samples == NULL)
        return kAudio_ParamError;

    AudioBufferList buffer_list;
    memset(&buffer_list, 0, sizeof(buffer_list));
    buffer_list.mNumberBuffers = 1;
    buffer_list.mBuffers[0].mNumberChannels = 1;
    buffer_list.mBuffers[0].mDataByteSize = number_frames * sizeof(int16_t);
    buffer_list.mBuffers[0].mData = samples;

    OSStatus status = AudioUnitRender(stream->unit, action_flags, timestamp, bus_number, number_frames, &buffer_list);
    if (status == noErr)
        ring_push(stream, samples, number_frames);
    free(samples);
    return status;
}

static OSStatus output_callback(
    void *ref_con,
    AudioUnitRenderActionFlags *action_flags,
    const AudioTimeStamp *timestamp,
    UInt32 bus_number,
    UInt32 number_frames,
    AudioBufferList *data)
{
    (void)action_flags;
    (void)timestamp;
    (void)bus_number;
    DvmAudioStream *stream = (DvmAudioStream *)ref_con;
    if (stream == NULL || data == NULL || data->mNumberBuffers == 0)
        return noErr;

    for (UInt32 buffer_index = 0; buffer_index < data->mNumberBuffers; buffer_index++) {
        AudioBuffer *buffer = &data->mBuffers[buffer_index];
        uint32_t capacity = buffer->mDataByteSize / sizeof(int16_t);
        if (capacity > number_frames)
            capacity = number_frames;
        if (buffer->mData == NULL)
            continue;

        uint32_t read = ring_pop(stream, (int16_t *)buffer->mData, capacity);
        if (read < capacity)
            memset(((int16_t *)buffer->mData) + read, 0, (capacity - read) * sizeof(int16_t));
    }
    return noErr;
}

static AudioStreamBasicDescription pcm_format(int32_t sample_rate)
{
    AudioStreamBasicDescription format;
    memset(&format, 0, sizeof(format));
    format.mSampleRate = sample_rate;
    format.mFormatID = kAudioFormatLinearPCM;
    format.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked;
    format.mBytesPerPacket = sizeof(int16_t);
    format.mFramesPerPacket = 1;
    format.mBytesPerFrame = sizeof(int16_t);
    format.mChannelsPerFrame = 1;
    format.mBitsPerChannel = 16;
    return format;
}

DvmAudioStream *dvm_audio_stream_create(
    uint64_t device_id,
    int32_t input,
    int32_t sample_rate,
    int32_t channels,
    int32_t bits_per_sample)
{
    if (sample_rate <= 0 || channels != 1 || bits_per_sample != 16)
        return NULL;

    AudioDeviceID audio_device = (AudioDeviceID)device_id;
    DvmAudioStream *stream = (DvmAudioStream *)calloc(1, sizeof(DvmAudioStream));
    if (stream == NULL)
        return NULL;

    stream->input = input != 0;
    // The Audio Unit is explicitly configured below for the requested PCM
    // format.  Report that stream format to the managed host as well: using
    // the hardware's nominal rate here made the host resample 8 kHz voice
    // audio a second time before it was written to this already-converted
    // Audio Unit.
    stream->sample_rate = (uint32_t)sample_rate;
    stream->ring_capacity = stream->sample_rate * DVM_AUDIO_RING_SECONDS + 1;
    stream->ring = (int16_t *)calloc(stream->ring_capacity, sizeof(int16_t));
    if (stream->ring == NULL) {
        free(stream);
        return NULL;
    }

    AudioComponentDescription description = {
        kAudioUnitType_Output,
        kAudioUnitSubType_HALOutput,
        kAudioUnitManufacturer_Apple,
        0,
        0};
    AudioComponent component = AudioComponentFindNext(NULL, &description);
    if (component == NULL || AudioComponentInstanceNew(component, &stream->unit) != noErr)
        goto fail;

    UInt32 enable = 1;
    if (stream->input) {
        UInt32 disable = 0;
        if (AudioUnitSetProperty(stream->unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &enable, sizeof(enable)) != noErr ||
            AudioUnitSetProperty(stream->unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, &disable, sizeof(disable)) != noErr)
            goto fail;
    } else if (AudioUnitSetProperty(stream->unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, &enable, sizeof(enable)) != noErr) {
        goto fail;
    }

    if (AudioUnitSetProperty(stream->unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &audio_device, sizeof(audio_device)) != noErr)
        goto fail;

    AudioStreamBasicDescription format = pcm_format(sample_rate);
    AudioUnitScope format_scope = stream->input ? kAudioUnitScope_Output : kAudioUnitScope_Input;
    AudioUnitElement format_element = stream->input ? 1 : 0;
    if (AudioUnitSetProperty(stream->unit, kAudioUnitProperty_StreamFormat, format_scope, format_element, &format, sizeof(format)) != noErr)
        goto fail;

    // HAL normally performs the conversion to the requested voice format,
    // but some device/driver combinations retain their native client rate.
    // Report the accepted format, not an assumed rate, so managed capture or
    // playback applies exactly one conversion when it is actually required.
    UInt32 format_size = sizeof(format);
    if (AudioUnitGetProperty(
            stream->unit,
            kAudioUnitProperty_StreamFormat,
            format_scope,
            format_element,
            &format,
            &format_size) != noErr ||
        format.mSampleRate <= 0)
        goto fail;
    stream->sample_rate = (uint32_t)format.mSampleRate;

    AURenderCallbackStruct callback = {
        stream->input ? input_callback : output_callback,
        stream};
    AudioUnitPropertyID callback_property = stream->input
        ? kAudioOutputUnitProperty_SetInputCallback
        : kAudioUnitProperty_SetRenderCallback;
    AudioUnitScope callback_scope = stream->input ? kAudioUnitScope_Global : kAudioUnitScope_Input;
    AudioUnitElement callback_element = stream->input ? 1 : 0;
    if (AudioUnitSetProperty(stream->unit, callback_property, callback_scope, callback_element, &callback, sizeof(callback)) != noErr)
        goto fail;

    atomic_init(&stream->read_index, 0);
    atomic_init(&stream->write_index, 0);
    atomic_init(&stream->running, 0);
    return stream;

fail:
    if (stream->unit != NULL) {
        AudioUnitUninitialize(stream->unit);
        AudioComponentInstanceDispose(stream->unit);
    }
    free(stream->ring);
    free(stream);
    return NULL;
}

int32_t dvm_audio_stream_start(DvmAudioStream *stream)
{
    if (stream == NULL)
        return -1;
    if (AudioUnitInitialize(stream->unit) != noErr)
        return -2;
    OSStatus status = AudioOutputUnitStart(stream->unit);
    if (status != noErr) {
        AudioUnitUninitialize(stream->unit);
        return (int32_t)status;
    }
    atomic_store_explicit(&stream->running, 1, memory_order_release);
    return 0;
}

int32_t dvm_audio_stream_stop(DvmAudioStream *stream)
{
    if (stream == NULL)
        return -1;
    if (!atomic_exchange_explicit(&stream->running, 0, memory_order_acq_rel))
        return 0;
    OSStatus status = AudioOutputUnitStop(stream->unit);
    AudioUnitUninitialize(stream->unit);
    return (int32_t)status;
}

int32_t dvm_audio_stream_get_sample_rate(DvmAudioStream *stream)
{
    if (stream == NULL)
        return -1;
    return (int32_t)stream->sample_rate;
}

int32_t dvm_audio_stream_read(DvmAudioStream *stream, int16_t *samples, uint32_t capacity)
{
    if (stream == NULL || samples == NULL)
        return -1;
    return (int32_t)ring_pop(stream, samples, capacity);
}

int32_t dvm_audio_stream_write(DvmAudioStream *stream, const int16_t *samples, uint32_t count)
{
    if (stream == NULL || samples == NULL)
        return -1;
    ring_push(stream, samples, count);
    return (int32_t)count;
}

void dvm_audio_stream_destroy(DvmAudioStream *stream)
{
    if (stream == NULL)
        return;
    dvm_audio_stream_stop(stream);
    if (stream->unit != NULL)
        AudioComponentInstanceDispose(stream->unit);
    free(stream->ring);
    free(stream);
}
