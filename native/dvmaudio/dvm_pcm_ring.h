#ifndef DVM_PCM_RING_H
#define DVM_PCM_RING_H

#include <stdatomic.h>
#include <stdint.h>

typedef struct DvmPcmRing {
    uint32_t capacity;
    int16_t *samples;
    _Atomic uint32_t read_index;
    _Atomic uint32_t write_index;
} DvmPcmRing;

int32_t dvm_pcm_ring_init(DvmPcmRing *ring, uint32_t usable_capacity);
void dvm_pcm_ring_dispose(DvmPcmRing *ring);
uint32_t dvm_pcm_ring_push(DvmPcmRing *ring, const int16_t *samples, uint32_t count);
uint32_t dvm_pcm_ring_pop(DvmPcmRing *ring, int16_t *samples, uint32_t capacity);
uint32_t dvm_pcm_ring_count(const DvmPcmRing *ring);

#endif
