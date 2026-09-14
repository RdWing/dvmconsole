// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
#ifndef DVM_AUDIO_IOS_H
#define DVM_AUDIO_IOS_H
#include <stdint.h>
typedef struct DvmIosAudio DvmIosAudio;
int32_t dvm_ios_audio_create(uint32_t sample_rate, int32_t input_enabled, DvmIosAudio **result);
int32_t dvm_ios_audio_start(DvmIosAudio *audio);
void dvm_ios_audio_set_output_enabled(DvmIosAudio *audio, int32_t enabled);
void dvm_ios_audio_stop_immediately(DvmIosAudio *audio);
void dvm_ios_audio_destroy(DvmIosAudio *audio);
uint32_t dvm_ios_audio_write(DvmIosAudio *audio, const int16_t *samples, uint32_t count);
void dvm_ios_audio_wake_input(DvmIosAudio *audio);
int32_t dvm_ios_audio_wait_input(DvmIosAudio *audio, int32_t timeout_ms);
uint32_t dvm_ios_audio_read(DvmIosAudio *audio, int16_t *samples, uint32_t count);
uint32_t dvm_ios_audio_queued_output(DvmIosAudio *audio);
uint64_t dvm_ios_audio_callback_count(DvmIosAudio *audio);
uint64_t dvm_ios_audio_dropped_input(DvmIosAudio *audio);
int32_t dvm_ios_audio_error(DvmIosAudio *audio);
uint64_t dvm_ios_audio_starved_samples(DvmIosAudio *audio);
uint64_t dvm_ios_audio_pending_starved_samples(DvmIosAudio *audio);
void dvm_ios_audio_end_playback_continuity(DvmIosAudio *audio);
#endif
