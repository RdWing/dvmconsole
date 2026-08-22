#include "dvm_pcm_ring.h"

#include <stdio.h>

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "ring check failed at line %d\n", __LINE__); \
        return __LINE__; \
    } \
} while (0)

int main(void)
{
    DvmPcmRing ring = {0};
    CHECK(dvm_pcm_ring_init(&ring, 4) == 0);
    CHECK(dvm_pcm_ring_count(&ring) == 0);

    const int16_t first[] = {1, 2, 3, 4, 5};
    CHECK(dvm_pcm_ring_push(&ring, first, 5) == 4);
    CHECK(dvm_pcm_ring_count(&ring) == 4);
    CHECK(dvm_pcm_ring_push(&ring, first, 1) == 0);

    int16_t output[4] = {0};
    CHECK(dvm_pcm_ring_pop(&ring, output, 2) == 2);
    CHECK(output[0] == 1 && output[1] == 2);
    CHECK(dvm_pcm_ring_count(&ring) == 2);

    const int16_t wrapped[] = {5, 6};
    CHECK(dvm_pcm_ring_push(&ring, wrapped, 2) == 2);
    CHECK(dvm_pcm_ring_pop(&ring, output, 4) == 4);
    CHECK(output[0] == 3 && output[1] == 4 && output[2] == 5 && output[3] == 6);
    CHECK(dvm_pcm_ring_count(&ring) == 0);

    dvm_pcm_ring_dispose(&ring);
    CHECK(dvm_pcm_ring_count(&ring) == 0);
    return 0;
}
