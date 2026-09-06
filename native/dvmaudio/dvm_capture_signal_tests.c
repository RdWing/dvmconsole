// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#include "dvm_capture_signal.h"

#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <time.h>

#define CHECK(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "capture signal check failed at line %d\n", __LINE__); \
        return __LINE__; \
    } \
} while (0)

typedef struct WaitContext {
    DvmCaptureSignal *signal;
    int result;
} WaitContext;

typedef struct NotifyContext {
    DvmCaptureSignal *signal;
    int notifications;
} NotifyContext;

static void *wait_indefinitely(void *context_pointer)
{
    WaitContext *context = context_pointer;
    context->result = dvm_capture_signal_wait(context->signal, -1);
    return NULL;
}

static void *notify_burst(void *context_pointer)
{
    NotifyContext *context = context_pointer;
    for (int index = 0; index < context->notifications; index++)
        dvm_capture_signal_notify(context->signal);
    return NULL;
}

static int64_t elapsed_nanoseconds(struct timespec start, struct timespec end)
{
    return (int64_t)(end.tv_sec - start.tv_sec) * 1000000000LL +
        (int64_t)(end.tv_nsec - start.tv_nsec);
}

int main(void)
{
    DvmCaptureSignal signal;
    CHECK(dvm_capture_signal_init(&signal) == 0);
    CHECK(dvm_capture_signal_wait(&signal, -2) == -1);
    CHECK(dvm_capture_signal_wait(&signal, 0) == 0);

    dvm_capture_signal_notify(&signal);
    dvm_capture_signal_notify(&signal);
    CHECK(dvm_capture_signal_wait(&signal, 0) == 1);
    CHECK(dvm_capture_signal_wait(&signal, 0) == 0);

    dvm_capture_signal_notify(&signal);
    CHECK(dvm_capture_signal_wait(&signal, 100) == 1);

    WaitContext context = {.signal = &signal, .result = 0};
    pthread_t waiter;
    CHECK(pthread_create(&waiter, NULL, wait_indefinitely, &context) == 0);
    dvm_capture_signal_notify(&signal);
    CHECK(pthread_join(waiter, NULL) == 0);
    CHECK(context.result == 1);

    for (int iteration = 0; iteration < 10000; iteration++) {
        dvm_capture_signal_notify(&signal);
        CHECK(dvm_capture_signal_wait(&signal, 100) == 1);
    }

    enum { producer_count = 4, notifications_per_producer = 25000 };
    pthread_t producers[producer_count];
    NotifyContext producer_context = {
        .signal = &signal,
        .notifications = notifications_per_producer};
    for (int index = 0; index < producer_count; index++)
        CHECK(pthread_create(&producers[index], NULL, notify_burst, &producer_context) == 0);
    for (int index = 0; index < producer_count; index++)
        CHECK(pthread_join(producers[index], NULL) == 0);
    CHECK(dvm_capture_signal_wait(&signal, 100) == 1);

    struct timespec callback_start;
    struct timespec callback_end;
    CHECK(clock_gettime(CLOCK_MONOTONIC, &callback_start) == 0);
    for (int iteration = 0; iteration < 100000; iteration++)
        dvm_capture_signal_notify(&signal);
    CHECK(clock_gettime(CLOCK_MONOTONIC, &callback_end) == 0);
    int64_t callback_elapsed = elapsed_nanoseconds(callback_start, callback_end);
    fprintf(stdout,
        "100000 coalesced capture notifications: %.3f ms\n",
        callback_elapsed / 1000000.0);
    CHECK(callback_elapsed < 5000000000LL);
    CHECK(dvm_capture_signal_wait(&signal, 100) == 1);

    dvm_capture_signal_dispose(&signal);

    for (int restart = 0; restart < 100; restart++) {
        CHECK(dvm_capture_signal_init(&signal) == 0);
        dvm_capture_signal_notify(&signal);
        CHECK(dvm_capture_signal_wait(&signal, 100) == 1);
        dvm_capture_signal_dispose(&signal);
    }
    return 0;
}
