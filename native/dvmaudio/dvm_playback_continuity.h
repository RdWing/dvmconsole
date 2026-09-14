// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
#ifndef DVM_PLAYBACK_CONTINUITY_H
#define DVM_PLAYBACK_CONTINUITY_H
#include <stdatomic.h>
#include <stdint.h>

// Missing samples remain provisional until playback resumes. Ending expected
// playback discards the ordinary idle tail rather than reporting it as a gap.
static inline void observe_playback_starvation(
    _Atomic int32_t *continuity_expected,
    _Atomic uint64_t *pending_starved_samples,
    uint32_t missing_samples)
{
    if (missing_samples == 0 ||
        !atomic_load_explicit(continuity_expected, memory_order_acquire))
        return;
    atomic_fetch_add_explicit(
        pending_starved_samples,
        missing_samples,
        memory_order_relaxed);
}

static inline void resume_playback_continuity(
    _Atomic int32_t *continuity_expected,
    _Atomic uint64_t *pending_starved_samples,
    _Atomic uint64_t *starved_samples)
{
    int32_t was_expected = atomic_exchange_explicit(
        continuity_expected,
        1,
        memory_order_acq_rel);
    uint64_t pending = atomic_exchange_explicit(
        pending_starved_samples,
        0,
        memory_order_acq_rel);
    if (was_expected && pending > 0)
        atomic_fetch_add_explicit(starved_samples, pending, memory_order_relaxed);
}

static inline void end_playback_continuity(
    _Atomic int32_t *continuity_expected,
    _Atomic uint64_t *pending_starved_samples)
{
    atomic_store_explicit(continuity_expected, 0, memory_order_release);
    atomic_store_explicit(pending_starved_samples, 0, memory_order_release);
}

#endif
