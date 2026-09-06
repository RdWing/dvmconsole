// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#include "dvm_capture_signal.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stddef.h>
#include <unistd.h>

static int configure_fd(int fd)
{
    int status_flags = fcntl(fd, F_GETFL, 0);
    if (status_flags < 0 || fcntl(fd, F_SETFL, status_flags | O_NONBLOCK) < 0)
        return -1;
    int descriptor_flags = fcntl(fd, F_GETFD, 0);
    if (descriptor_flags < 0 || fcntl(fd, F_SETFD, descriptor_flags | FD_CLOEXEC) < 0)
        return -1;
    return 0;
}

int dvm_capture_signal_init(DvmCaptureSignal *signal)
{
    if (signal == NULL)
        return -1;
    signal->read_fd = -1;
    signal->write_fd = -1;
    atomic_init(&signal->pending, 0);

    int descriptors[2];
    if (pipe(descriptors) != 0)
        return -1;
    signal->read_fd = descriptors[0];
    signal->write_fd = descriptors[1];
    if (configure_fd(signal->read_fd) == 0 && configure_fd(signal->write_fd) == 0)
        return 0;

    dvm_capture_signal_dispose(signal);
    return -1;
}

void dvm_capture_signal_notify(DvmCaptureSignal *signal)
{
    if (signal == NULL || signal->write_fd < 0 ||
        atomic_exchange_explicit(&signal->pending, 1, memory_order_acq_rel))
        return;

    const uint8_t marker = 1;
    ssize_t written;
    do {
        written = write(signal->write_fd, &marker, sizeof(marker));
    } while (written < 0 && errno == EINTR);
    if (written == (ssize_t)sizeof(marker))
        return;
    if (written < 0 && errno == EAGAIN)
        return;
    atomic_store_explicit(&signal->pending, 0, memory_order_release);
}

int dvm_capture_signal_wait(DvmCaptureSignal *signal, int32_t timeout_ms)
{
    if (signal == NULL || signal->read_fd < 0 || timeout_ms < -1)
        return -1;

    struct pollfd descriptor = {
        .fd = signal->read_fd,
        .events = POLLIN,
        .revents = 0};
    int result;
    do {
        result = poll(&descriptor, 1, timeout_ms);
    } while (result < 0 && errno == EINTR);
    if (result <= 0)
        return result;
    if ((descriptor.revents & POLLIN) == 0)
        return -1;

    // Rearm before consuming the marker. A callback racing this drain can
    // then publish the next marker instead of observing stale pending state
    // that the consumer is about to clear.
    atomic_store_explicit(&signal->pending, 0, memory_order_release);
    uint8_t marker;
    ssize_t read_count;
    do {
        read_count = read(signal->read_fd, &marker, sizeof(marker));
    } while (read_count < 0 && errno == EINTR);
    if (read_count == (ssize_t)sizeof(marker))
        return 1;
    if (read_count < 0 && errno == EAGAIN)
        return 0;
    return -1;
}

void dvm_capture_signal_dispose(DvmCaptureSignal *signal)
{
    if (signal == NULL)
        return;
    if (signal->read_fd >= 0)
        close(signal->read_fd);
    if (signal->write_fd >= 0)
        close(signal->write_fd);
    signal->read_fd = -1;
    signal->write_fd = -1;
    atomic_store_explicit(&signal->pending, 0, memory_order_release);
}
