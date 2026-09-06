// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#ifndef DVM_CAPTURE_SIGNAL_H
#define DVM_CAPTURE_SIGNAL_H

#include <stdatomic.h>
#include <stdint.h>

typedef struct DvmCaptureSignal {
    int read_fd;
    int write_fd;
    _Atomic int pending;
} DvmCaptureSignal;

int dvm_capture_signal_init(DvmCaptureSignal *signal);
void dvm_capture_signal_notify(DvmCaptureSignal *signal);
int dvm_capture_signal_wait(DvmCaptureSignal *signal, int32_t timeout_ms);
void dvm_capture_signal_dispose(DvmCaptureSignal *signal);

#endif
