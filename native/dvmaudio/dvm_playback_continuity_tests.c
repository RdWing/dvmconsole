// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
#include "dvm_playback_continuity.h"
#include <assert.h>

int main(void)
{
    _Atomic int32_t expected = 0;
    _Atomic uint64_t pending = 0, committed = 0;
    observe_playback_starvation(&expected, &pending, 160);
    assert(atomic_load(&pending) == 0); // Idle callbacks are not gaps.
    resume_playback_continuity(&expected, &pending, &committed);
    observe_playback_starvation(&expected, &pending, 80);
    assert(atomic_load(&pending) == 80);
    assert(atomic_load(&committed) == 0);
    resume_playback_continuity(&expected, &pending, &committed);
    assert(atomic_load(&pending) == 0);
    assert(atomic_load(&committed) == 80); // A resumed stream confirms the gap.
    observe_playback_starvation(&expected, &pending, 160);
    end_playback_continuity(&expected, &pending);
    assert(atomic_load(&pending) == 0); // Normal end discards the idle tail.
    observe_playback_starvation(&expected, &pending, 8000);
    resume_playback_continuity(&expected, &pending, &committed);
    assert(atomic_load(&committed) == 80);
    return 0;
}
