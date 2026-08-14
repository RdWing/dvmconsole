#ifndef DVM_AUDIO_H
#define DVM_AUDIO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DvmAudioStream DvmAudioStream;

int32_t dvm_audio_get_device_count(int32_t input, int32_t *count);
int32_t dvm_audio_get_device(
    int32_t input,
    int32_t index,
    uint64_t *device_id,
    char *name,
    uint32_t name_capacity,
    int32_t *is_default);

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
void dvm_audio_stream_destroy(DvmAudioStream *stream);

#ifdef __cplusplus
}
#endif

#endif
