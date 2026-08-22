#include "dvm_pcm_ring.h"

#include <stdlib.h>
#include <string.h>

int32_t dvm_pcm_ring_init(DvmPcmRing *ring, uint32_t usable_capacity)
{
    if (ring == NULL || usable_capacity == 0 || usable_capacity == UINT32_MAX)
        return -1;

    ring->capacity = usable_capacity + 1;
    ring->samples = (int16_t *)calloc(ring->capacity, sizeof(int16_t));
    if (ring->samples == NULL) {
        ring->capacity = 0;
        return -2;
    }
    atomic_init(&ring->read_index, 0);
    atomic_init(&ring->write_index, 0);
    return 0;
}

void dvm_pcm_ring_dispose(DvmPcmRing *ring)
{
    if (ring == NULL)
        return;
    free(ring->samples);
    ring->samples = NULL;
    ring->capacity = 0;
    atomic_store_explicit(&ring->read_index, 0, memory_order_relaxed);
    atomic_store_explicit(&ring->write_index, 0, memory_order_relaxed);
}

uint32_t dvm_pcm_ring_push(DvmPcmRing *ring, const int16_t *samples, uint32_t count)
{
    if (ring == NULL || ring->samples == NULL || samples == NULL || ring->capacity < 2)
        return 0;

    uint32_t write = atomic_load_explicit(&ring->write_index, memory_order_relaxed);
    uint32_t read = atomic_load_explicit(&ring->read_index, memory_order_acquire);
    uint32_t available = read > write
        ? read - write - 1
        : ring->capacity - write + read - 1;
    uint32_t accepted = count < available ? count : available;
    uint32_t first = accepted < ring->capacity - write
        ? accepted
        : ring->capacity - write;
    memcpy(ring->samples + write, samples, first * sizeof(int16_t));
    memcpy(ring->samples, samples + first, (accepted - first) * sizeof(int16_t));
    atomic_store_explicit(
        &ring->write_index,
        (write + accepted) % ring->capacity,
        memory_order_release);
    return accepted;
}

uint32_t dvm_pcm_ring_pop(DvmPcmRing *ring, int16_t *samples, uint32_t capacity)
{
    if (ring == NULL || ring->samples == NULL || samples == NULL || ring->capacity < 2)
        return 0;

    uint32_t read = atomic_load_explicit(&ring->read_index, memory_order_relaxed);
    uint32_t write = atomic_load_explicit(&ring->write_index, memory_order_acquire);
    uint32_t available = write >= read
        ? write - read
        : ring->capacity - read + write;
    uint32_t count = capacity < available ? capacity : available;
    uint32_t first = count < ring->capacity - read
        ? count
        : ring->capacity - read;
    memcpy(samples, ring->samples + read, first * sizeof(int16_t));
    memcpy(samples + first, ring->samples, (count - first) * sizeof(int16_t));
    atomic_store_explicit(
        &ring->read_index,
        (read + count) % ring->capacity,
        memory_order_release);
    return count;
}

uint32_t dvm_pcm_ring_count(const DvmPcmRing *ring)
{
    if (ring == NULL || ring->samples == NULL || ring->capacity < 2)
        return 0;
    uint32_t read = atomic_load_explicit(&ring->read_index, memory_order_acquire);
    uint32_t write = atomic_load_explicit(&ring->write_index, memory_order_acquire);
    return write >= read
        ? write - read
        : ring->capacity - read + write;
}
