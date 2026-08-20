#ifndef DVM_AUDIO_H
#define DVM_AUDIO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DvmAudioStream DvmAudioStream;
typedef struct DvmVoiceProcessingStream DvmVoiceProcessingStream;

enum DvmHighQualityBluetoothStatus {
    DVM_HIGH_QUALITY_BLUETOOTH_OFF = 0,
    DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE = 1,
    DVM_HIGH_QUALITY_BLUETOOTH_REQUESTED = 2,
    DVM_HIGH_QUALITY_BLUETOOTH_ACTIVE = 3,
    DVM_HIGH_QUALITY_BLUETOOTH_UNSUPPORTED = 4
};

enum DvmPermissionRequestResult {
    DVM_PERMISSION_UNAVAILABLE = 0,
    DVM_PERMISSION_GRANTED = 1,
    DVM_PERMISSION_REQUESTED = 2,
    DVM_PERMISSION_DENIED = 3,
    DVM_PERMISSION_RESTRICTED = 4
};

// Requests macOS microphone authorization. A REQUESTED result means the
// system prompt was started asynchronously and the user has not responded yet.
int32_t dvm_audio_request_microphone_permission(void);

int32_t dvm_audio_get_device_count(int32_t input, int32_t *count);
int32_t dvm_audio_get_device(
    int32_t input,
    int32_t index,
    uint64_t *device_id,
    char *name,
    uint32_t name_capacity,
    int32_t *is_default);

// Returns 1 for a Bluetooth or Bluetooth LE CoreAudio endpoint, 0 for a
// known non-Bluetooth endpoint, and -1 when CoreAudio cannot currently
// classify the device (for example while a route is changing).
int32_t dvm_audio_device_is_bluetooth(uint64_t device_id);

// Attempts the macOS 26 full-bandwidth Bluetooth recording mode for the
// system-default Bluetooth input/output pair. A zero result means the route is
// ineligible or unsupported and callers should continue with normal CoreAudio.
// The session is process-global and reference counted.
int32_t dvm_audio_high_quality_bluetooth_acquire(
    uint64_t input_device_id,
    uint64_t output_device_id);
void dvm_audio_high_quality_bluetooth_release(void);
int32_t dvm_audio_high_quality_bluetooth_status(void);

DvmAudioStream *dvm_audio_stream_create(
    uint64_t device_id,
    int32_t input,
    int32_t sample_rate,
    int32_t channels,
    int32_t bits_per_sample);
int32_t dvm_audio_stream_start(DvmAudioStream *stream);
int32_t dvm_audio_stream_stop(DvmAudioStream *stream);
int32_t dvm_audio_stream_get_sample_rate(DvmAudioStream *stream);
int32_t dvm_audio_stream_read(DvmAudioStream *stream, int16_t *samples, uint32_t capacity);
int32_t dvm_audio_stream_write(DvmAudioStream *stream, const int16_t *samples, uint32_t count);
uint32_t dvm_audio_stream_queued_samples(DvmAudioStream *stream);
void dvm_audio_stream_destroy(DvmAudioStream *stream);

// One full-duplex Voice Processing I/O unit. Playback written here is the
// echo-reference signal used by Apple's microphone AEC/AGC processing.
DvmVoiceProcessingStream *dvm_audio_voice_processing_create(
    uint64_t input_device_id,
    uint64_t output_device_id,
    int32_t sample_rate,
    int32_t channels,
    int32_t bits_per_sample);
int32_t dvm_audio_voice_processing_start(DvmVoiceProcessingStream *stream);
int32_t dvm_audio_voice_processing_stop(DvmVoiceProcessingStream *stream);
int32_t dvm_audio_voice_processing_read(
    DvmVoiceProcessingStream *stream,
    int16_t *samples,
    uint32_t capacity);
int32_t dvm_audio_voice_processing_write(
    DvmVoiceProcessingStream *stream,
    const int16_t *samples,
    uint32_t count);
uint32_t dvm_audio_voice_processing_queued_samples(DvmVoiceProcessingStream *stream);
void dvm_audio_voice_processing_destroy(DvmVoiceProcessingStream *stream);

#ifdef __cplusplus
}
#endif

#endif
