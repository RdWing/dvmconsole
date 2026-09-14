// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#include "dvmaudio_ios.h"
#include "../dvmaudio/dvm_pcm_ring.h"
#include "../dvmaudio/dvm_capture_signal.h"
#include "../dvmaudio/dvm_playback_continuity.h"
#include <AudioToolbox/AudioToolbox.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define DVM_IOS_MAXIMUM_FRAMES 4096u

struct DvmIosAudio {
    AudioUnit unit;
    DvmPcmRing output;
    DvmPcmRing input;
    DvmCaptureSignal capture_signal;
    int16_t *capture_buffer;
    _Atomic int32_t running;
    _Atomic int32_t output_enabled;
    _Atomic int32_t clear_output;
    _Atomic int32_t error;
    _Atomic uint64_t callbacks;
    _Atomic uint64_t dropped_input;
    _Atomic int32_t playback_continuity_expected;
    _Atomic uint64_t pending_starved_samples;
    _Atomic uint64_t starved_samples;
};

static OSStatus render_output(void *context, AudioUnitRenderActionFlags *flags,
    const AudioTimeStamp *timestamp, UInt32 bus, UInt32 frames, AudioBufferList *buffers)
{
    (void)flags; (void)timestamp; (void)bus;
    DvmIosAudio *audio = context;
    atomic_fetch_add_explicit(&audio->callbacks, 1, memory_order_relaxed);
    if (buffers == NULL) return noErr;
    // The negotiated client format is stereo interleaved Int16. Unexpected
    // layouts are silenced; a callback never allocates a replacement buffer.
    for (UInt32 index = 0; index < buffers->mNumberBuffers; index++) {
        AudioBuffer *buffer = &buffers->mBuffers[index];
        if (buffer->mData != NULL) memset(buffer->mData, 0, buffer->mDataByteSize);
    }
    if (atomic_exchange_explicit(&audio->clear_output, 0, memory_order_acq_rel)) {
        // Only the callback (the ring consumer) advances its read position.
        uint32_t write = atomic_load_explicit(&audio->output.write_index, memory_order_acquire);
        atomic_store_explicit(&audio->output.read_index, write, memory_order_release);
    }
    if (!atomic_load_explicit(&audio->running, memory_order_acquire) ||
        !atomic_load_explicit(&audio->output_enabled, memory_order_acquire)) return noErr;
    if (frames > DVM_IOS_MAXIMUM_FRAMES || buffers->mNumberBuffers != 1 ||
        buffers->mBuffers[0].mData == NULL || buffers->mBuffers[0].mDataByteSize < frames * 4u) {
        atomic_store_explicit(&audio->error, kAudio_ParamError, memory_order_release);
        return noErr;
    }
    uint32_t received = dvm_pcm_ring_pop(&audio->output, buffers->mBuffers[0].mData, frames * 2u);
    observe_playback_starvation(&audio->playback_continuity_expected,
        &audio->pending_starved_samples, frames * 2u - received);
    return noErr;
}

static OSStatus capture_input(void *context, AudioUnitRenderActionFlags *flags,
    const AudioTimeStamp *timestamp, UInt32 bus, UInt32 frames, AudioBufferList *unused)
{
    (void)unused;
    DvmIosAudio *audio = context;
    if (!atomic_load_explicit(&audio->running, memory_order_acquire)) return noErr;
    if (frames > DVM_IOS_MAXIMUM_FRAMES) {
        atomic_store_explicit(&audio->error, kAudio_ParamError, memory_order_release);
        dvm_capture_signal_notify(&audio->capture_signal);
        return noErr;
    }
    AudioBufferList buffers = { .mNumberBuffers = 1,
        .mBuffers = {{ .mNumberChannels = 1, .mDataByteSize = frames * 2u, .mData = audio->capture_buffer }} };
    OSStatus result = AudioUnitRender(audio->unit, flags, timestamp, bus, frames, &buffers);
    if (result != noErr) {
        atomic_store_explicit(&audio->error, result, memory_order_release);
        dvm_capture_signal_notify(&audio->capture_signal);
        return noErr;
    }
    uint32_t accepted = dvm_pcm_ring_push(&audio->input, audio->capture_buffer, frames);
    atomic_fetch_add_explicit(&audio->dropped_input, frames - accepted, memory_order_relaxed);
    if (accepted > 0) dvm_capture_signal_notify(&audio->capture_signal);
    return noErr;
}

static AudioStreamBasicDescription pcm_format(uint32_t rate, uint32_t channels)
{
    AudioStreamBasicDescription format = {0};
    format.mSampleRate = rate;
    format.mFormatID = kAudioFormatLinearPCM;
    format.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked;
    format.mBytesPerPacket = channels * 2u;
    format.mFramesPerPacket = 1;
    format.mBytesPerFrame = channels * 2u;
    format.mChannelsPerFrame = channels;
    format.mBitsPerChannel = 16;
    return format;
}

int32_t dvm_ios_audio_create(uint32_t sample_rate, int32_t input_enabled, DvmIosAudio **result)
{
    if (result == NULL || sample_rate < 8000 || sample_rate > 192000) return kAudio_ParamError;
    *result = NULL;
    DvmIosAudio *audio = calloc(1, sizeof(*audio));
    if (audio == NULL) return -1;
    if (dvm_capture_signal_init(&audio->capture_signal) != 0) { free(audio); return -1; }
    atomic_init(&audio->running, 0);
    atomic_init(&audio->output_enabled, 1);
    atomic_init(&audio->clear_output, 0);
    atomic_init(&audio->error, 0);
    atomic_init(&audio->callbacks, 0);
    atomic_init(&audio->dropped_input, 0);
    atomic_init(&audio->playback_continuity_expected, 0);
    atomic_init(&audio->pending_starved_samples, 0);
    atomic_init(&audio->starved_samples, 0);
    OSStatus status = -1;
    // Two seconds maximum in each direction. Only one managed producer and
    // one native consumer (or the reverse for capture) may access each ring.
    if (dvm_pcm_ring_init(&audio->output, sample_rate * 4u) != 0 ||
        dvm_pcm_ring_init(&audio->input, sample_rate * 2u) != 0) goto fail;
    audio->capture_buffer = calloc(DVM_IOS_MAXIMUM_FRAMES, sizeof(int16_t));
    if (audio->capture_buffer == NULL) goto fail;
    AudioComponentDescription description = { .componentType = kAudioUnitType_Output,
        .componentSubType = kAudioUnitSubType_RemoteIO, .componentManufacturer = kAudioUnitManufacturer_Apple };
    AudioComponent component = AudioComponentFindNext(NULL, &description);
    if (component == NULL) goto fail;
    status = AudioComponentInstanceNew(component, &audio->unit);
    if (status != noErr) goto fail;
    UInt32 enabled = input_enabled ? 1u : 0u;
    status = AudioUnitSetProperty(audio->unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &enabled, sizeof(enabled));
    if (status != noErr) goto fail;
    UInt32 maximum = DVM_IOS_MAXIMUM_FRAMES;
    status = AudioUnitSetProperty(audio->unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maximum, sizeof(maximum));
    if (status != noErr) goto fail;
    AudioStreamBasicDescription output = pcm_format(sample_rate, 2);
    status = AudioUnitSetProperty(audio->unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &output, sizeof(output));
    if (status != noErr) goto fail;
    AURenderCallbackStruct render = { .inputProc = render_output, .inputProcRefCon = audio };
    status = AudioUnitSetProperty(audio->unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0, &render, sizeof(render));
    if (status != noErr) goto fail;
    if (input_enabled) {
        AudioStreamBasicDescription input = pcm_format(sample_rate, 1);
        status = AudioUnitSetProperty(audio->unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, &input, sizeof(input));
        if (status != noErr) goto fail;
        UInt32 allocate = 0;
        status = AudioUnitSetProperty(audio->unit, kAudioUnitProperty_ShouldAllocateBuffer, kAudioUnitScope_Output, 1, &allocate, sizeof(allocate));
        if (status != noErr) goto fail;
        AURenderCallbackStruct capture = { .inputProc = capture_input, .inputProcRefCon = audio };
        status = AudioUnitSetProperty(audio->unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 1, &capture, sizeof(capture));
        if (status != noErr) goto fail;
    }
    status = AudioUnitInitialize(audio->unit);
    if (status != noErr) goto fail;
    *result = audio;
    return noErr;
fail:
    dvm_ios_audio_destroy(audio);
    return status;
}

int32_t dvm_ios_audio_start(DvmIosAudio *audio)
{
    if (audio == NULL) return kAudio_ParamError;
    atomic_store_explicit(&audio->running, 1, memory_order_release);
    OSStatus status = AudioOutputUnitStart(audio->unit);
    if (status != noErr) atomic_store_explicit(&audio->running, 0, memory_order_release);
    return status;
}

void dvm_ios_audio_set_output_enabled(DvmIosAudio *audio, int32_t enabled)
{
    if (audio == NULL) return;
    if (!enabled) {
        atomic_store_explicit(&audio->clear_output, 1, memory_order_release);
        dvm_ios_audio_end_playback_continuity(audio);
    }
    atomic_store_explicit(&audio->output_enabled, enabled != 0, memory_order_release);
}

void dvm_ios_audio_stop_immediately(DvmIosAudio *audio)
{
    if (audio != NULL) {
        atomic_store_explicit(&audio->running, 0, memory_order_release);
        dvm_capture_signal_notify(&audio->capture_signal);
        dvm_ios_audio_end_playback_continuity(audio);
    }
}

void dvm_ios_audio_destroy(DvmIosAudio *audio)
{
    if (audio == NULL) return;
    dvm_ios_audio_stop_immediately(audio);
    if (audio->unit != NULL) {
        AudioOutputUnitStop(audio->unit);
        AudioUnitUninitialize(audio->unit);
        AudioComponentInstanceDispose(audio->unit);
    }
    dvm_capture_signal_dispose(&audio->capture_signal);
    dvm_pcm_ring_dispose(&audio->output);
    dvm_pcm_ring_dispose(&audio->input);
    free(audio->capture_buffer);
    free(audio);
}

uint32_t dvm_ios_audio_write(DvmIosAudio *audio, const int16_t *samples, uint32_t count)
{
    if (audio == NULL || count % 2u != 0) return 0;
    uint32_t accepted = dvm_pcm_ring_push(&audio->output, samples, count);
    if (accepted > 0)
        resume_playback_continuity(&audio->playback_continuity_expected,
            &audio->pending_starved_samples, &audio->starved_samples);
    return accepted;
}
uint32_t dvm_ios_audio_read(DvmIosAudio *audio, int16_t *samples, uint32_t count)
{
    return audio == NULL ? 0 : dvm_pcm_ring_pop(&audio->input, samples, count);
}
uint32_t dvm_ios_audio_queued_output(DvmIosAudio *audio)
{
    return audio == NULL ? 0 : dvm_pcm_ring_count(&audio->output);
}
uint64_t dvm_ios_audio_callback_count(DvmIosAudio *audio)
{
    return audio == NULL ? 0 : atomic_load_explicit(&audio->callbacks, memory_order_acquire);
}
uint64_t dvm_ios_audio_dropped_input(DvmIosAudio *audio)
{
    return audio == NULL ? 0 : atomic_load_explicit(&audio->dropped_input, memory_order_acquire);
}
int32_t dvm_ios_audio_error(DvmIosAudio *audio)
{
    return audio == NULL ? kAudio_ParamError : atomic_load_explicit(&audio->error, memory_order_acquire);
}

uint64_t dvm_ios_audio_starved_samples(DvmIosAudio *audio)
{
    return audio == NULL ? 0 : atomic_load_explicit(&audio->starved_samples, memory_order_acquire);
}
uint64_t dvm_ios_audio_pending_starved_samples(DvmIosAudio *audio)
{
    return audio == NULL ? 0 : atomic_load_explicit(&audio->pending_starved_samples, memory_order_acquire);
}
void dvm_ios_audio_end_playback_continuity(DvmIosAudio *audio)
{
    if (audio != NULL)
        end_playback_continuity(&audio->playback_continuity_expected, &audio->pending_starved_samples);
}

int32_t dvm_ios_audio_wait_input(DvmIosAudio *audio, int32_t timeout_ms)
{
    if (audio == NULL) return -1;
    if (dvm_pcm_ring_count(&audio->input) > 0) return 1;
    return dvm_capture_signal_wait(&audio->capture_signal, timeout_ms);
}

void dvm_ios_audio_wake_input(DvmIosAudio *audio)
{
    if (audio != NULL) dvm_capture_signal_notify(&audio->capture_signal);
}
