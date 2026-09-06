// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

#include "../dvmaudio/dvmaudio.h"
#include "../dvmaudio/dvm_capture_signal.h"
#include "../dvmaudio/dvm_pcm_ring.h"

#include <pipewire/pipewire.h>
#include <pipewire/extensions/metadata.h>
#include <spa/param/audio/format-utils.h>
#include <spa/utils/dict.h>

#include <pthread.h>
#include <stdatomic.h>
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(__GNUC__) || defined(__clang__)
#define DVM_EXPORT __attribute__((visibility("default")))
#else
#define DVM_EXPORT
#endif

enum {
    DVM_AUDIO_OK = 0,
    DVM_AUDIO_INVALID_ARGUMENT = -1,
    DVM_AUDIO_ALLOCATION_FAILED = -2,
    DVM_AUDIO_PIPEWIRE_FAILED = -3,
    DVM_AUDIO_NOT_FOUND = -4,
    DVM_AUDIO_STREAM_FAILED = -5
};

enum {
    DVM_AUDIO_MAX_DEVICES = 128,
    DVM_AUDIO_DEVICE_NAME_CAPACITY = 256
};

typedef struct {
    uint64_t id;
    char name[DVM_AUDIO_DEVICE_NAME_CAPACITY];
    int32_t bluetooth;
} DvmAudioDevice;

typedef struct {
    DvmAudioDevice inputs[DVM_AUDIO_MAX_DEVICES];
    DvmAudioDevice outputs[DVM_AUDIO_MAX_DEVICES];
    int32_t input_count;
    int32_t output_count;
} DvmAudioDeviceSnapshot;

typedef struct {
    struct pw_main_loop *loop;
    int sync_sequence;
    int failed;
    DvmAudioDeviceSnapshot *snapshot;
} DvmAudioDiscovery;

typedef void (*DvmAudioDeviceChangedCallback)(void *user_data);

typedef struct {
    struct pw_thread_loop *loop;
    struct pw_context *context;
    struct pw_core *core;
    struct pw_registry *registry;
    struct pw_metadata *metadata;
    struct spa_hook core_listener;
    struct spa_hook registry_listener;
    struct spa_hook metadata_listener;
    uint32_t audio_node_ids[DVM_AUDIO_MAX_DEVICES * 2];
    size_t audio_node_count;
    int initial_sync_sequence;
    int initialized;
    DvmAudioDeviceChangedCallback callback;
    void *user_data;
} DvmAudioDeviceMonitor;

static pthread_mutex_t device_snapshot_mutex = PTHREAD_MUTEX_INITIALIZER;
static DvmAudioDeviceSnapshot device_snapshot;

struct DvmAudioStream {
    struct pw_thread_loop *loop;
    struct pw_stream *stream;
    DvmPcmRing ring;
    DvmCaptureSignal capture_signal;
    int32_t input;
    int32_t sample_rate;
    int32_t channels;
    _Atomic int32_t state;
    _Atomic int32_t continuity_active;
    _Atomic uint64_t starved_samples;
    _Atomic uint64_t pending_starved_samples;
    _Atomic uint64_t output_callback_count;
};

static void initialize_pipewire_once(void)
{
    pw_init(NULL, NULL);
}

static void initialize_pipewire(void)
{
    static pthread_once_t once = PTHREAD_ONCE_INIT;
    pthread_once(&once, initialize_pipewire_once);
}

static void copy_device_name(char *destination, const char *source)
{
    if (source == NULL || source[0] == '\0')
        source = "Unnamed PipeWire device";
    size_t length = strlen(source);
    if (length >= DVM_AUDIO_DEVICE_NAME_CAPACITY)
        length = DVM_AUDIO_DEVICE_NAME_CAPACITY - 1;
    memcpy(destination, source, length);
    destination[length] = '\0';
}

static int is_bluetooth_node(const struct spa_dict *properties)
{
    const char *device_api = spa_dict_lookup(properties, PW_KEY_DEVICE_API);
    const char *node_name = spa_dict_lookup(properties, PW_KEY_NODE_NAME);
    return (device_api != NULL && strstr(device_api, "bluez") != NULL) ||
        (node_name != NULL && strstr(node_name, "bluez") != NULL);
}

static void on_registry_global(
    void *user_data,
    uint32_t id,
    uint32_t permissions,
    const char *type,
    uint32_t version,
    const struct spa_dict *properties)
{
    (void)id;
    (void)permissions;
    (void)version;
    DvmAudioDiscovery *discovery = user_data;
    if (properties == NULL || strcmp(type, PW_TYPE_INTERFACE_Node) != 0)
        return;

    const char *media_class = spa_dict_lookup(properties, PW_KEY_MEDIA_CLASS);
    DvmAudioDevice *devices;
    int32_t *count;
    if (media_class != NULL && strcmp(media_class, "Audio/Source") == 0) {
        devices = discovery->snapshot->inputs;
        count = &discovery->snapshot->input_count;
    }
    else if (media_class != NULL && strcmp(media_class, "Audio/Sink") == 0) {
        devices = discovery->snapshot->outputs;
        count = &discovery->snapshot->output_count;
    }
    else {
        return;
    }
    if (*count >= DVM_AUDIO_MAX_DEVICES)
        return;

    const char *serial_text = spa_dict_lookup(properties, PW_KEY_OBJECT_SERIAL);
    if (serial_text == NULL || serial_text[0] == '\0')
        return;
    char *serial_end = NULL;
    uint64_t serial = strtoull(serial_text, &serial_end, 10);
    if (serial == 0 || serial_end == serial_text || *serial_end != '\0')
        return;

    DvmAudioDevice *device = &devices[*count];
    device->id = serial;
    device->bluetooth = is_bluetooth_node(properties);
    const char *display_name = spa_dict_lookup(properties, PW_KEY_NODE_DESCRIPTION);
    if (display_name == NULL || display_name[0] == '\0')
        display_name = spa_dict_lookup(properties, PW_KEY_NODE_NICK);
    if (display_name == NULL || display_name[0] == '\0')
        display_name = spa_dict_lookup(properties, PW_KEY_NODE_NAME);
    copy_device_name(device->name, display_name);
    (*count)++;
}

static void on_core_done(void *user_data, uint32_t id, int sequence)
{
    (void)id;
    DvmAudioDiscovery *discovery = user_data;
    if (sequence == discovery->sync_sequence)
        pw_main_loop_quit(discovery->loop);
}

static void on_core_error(
    void *user_data,
    uint32_t id,
    int sequence,
    int result,
    const char *message)
{
    (void)id;
    (void)sequence;
    (void)result;
    (void)message;
    DvmAudioDiscovery *discovery = user_data;
    discovery->failed = 1;
    pw_main_loop_quit(discovery->loop);
}

static const struct pw_registry_events registry_events = {
    PW_VERSION_REGISTRY_EVENTS,
    .global = on_registry_global
};

static const struct pw_core_events core_events = {
    PW_VERSION_CORE_EVENTS,
    .done = on_core_done,
    .error = on_core_error
};

static void notify_device_monitor(DvmAudioDeviceMonitor *monitor)
{
    if (monitor->initialized != 0 && monitor->callback != NULL)
        monitor->callback(monitor->user_data);
}

static void remember_audio_node(DvmAudioDeviceMonitor *monitor, uint32_t id)
{
    for (size_t index = 0; index < monitor->audio_node_count; index++) {
        if (monitor->audio_node_ids[index] == id)
            return;
    }
    if (monitor->audio_node_count < DVM_AUDIO_MAX_DEVICES * 2)
        monitor->audio_node_ids[monitor->audio_node_count++] = id;
}

static int forget_audio_node(DvmAudioDeviceMonitor *monitor, uint32_t id)
{
    for (size_t index = 0; index < monitor->audio_node_count; index++) {
        if (monitor->audio_node_ids[index] != id)
            continue;
        monitor->audio_node_ids[index] =
            monitor->audio_node_ids[monitor->audio_node_count - 1];
        monitor->audio_node_count--;
        return 1;
    }
    return 0;
}

static int on_device_metadata_property(
    void *user_data,
    uint32_t subject,
    const char *key,
    const char *type,
    const char *value)
{
    (void)subject;
    (void)type;
    (void)value;
    if (key != NULL &&
        (strcmp(key, "default.audio.source") == 0 ||
         strcmp(key, "default.audio.sink") == 0)) {
        notify_device_monitor(user_data);
    }
    return 0;
}

static const struct pw_metadata_events device_metadata_events = {
    PW_VERSION_METADATA_EVENTS,
    .property = on_device_metadata_property
};

static void on_device_registry_global(
    void *user_data,
    uint32_t id,
    uint32_t permissions,
    const char *type,
    uint32_t version,
    const struct spa_dict *properties)
{
    (void)permissions;
    (void)version;
    DvmAudioDeviceMonitor *monitor = user_data;
    if (properties == NULL)
        return;

    if (strcmp(type, PW_TYPE_INTERFACE_Node) == 0) {
        const char *media_class = spa_dict_lookup(properties, PW_KEY_MEDIA_CLASS);
        if (media_class != NULL &&
            (strcmp(media_class, "Audio/Source") == 0 ||
             strcmp(media_class, "Audio/Sink") == 0)) {
            remember_audio_node(monitor, id);
            notify_device_monitor(monitor);
        }
        return;
    }

    if (monitor->metadata == NULL && strcmp(type, PW_TYPE_INTERFACE_Metadata) == 0) {
        const char *metadata_name = spa_dict_lookup(properties, PW_KEY_METADATA_NAME);
        if (metadata_name != NULL && strcmp(metadata_name, "default") == 0) {
            monitor->metadata = pw_registry_bind(
                monitor->registry,
                id,
                PW_TYPE_INTERFACE_Metadata,
                PW_VERSION_METADATA,
                0);
            if (monitor->metadata != NULL) {
                pw_metadata_add_listener(
                    monitor->metadata,
                    &monitor->metadata_listener,
                    &device_metadata_events,
                    monitor);
            }
        }
    }
}

static void on_device_registry_global_remove(void *user_data, uint32_t id)
{
    DvmAudioDeviceMonitor *monitor = user_data;
    if (forget_audio_node(monitor, id))
        notify_device_monitor(monitor);
}

static void on_device_core_error(
    void *user_data,
    uint32_t id,
    int sequence,
    int result,
    const char *message)
{
    (void)id;
    (void)sequence;
    (void)result;
    (void)message;
    notify_device_monitor(user_data);
}

static void on_device_core_done(void *user_data, uint32_t id, int sequence)
{
    (void)id;
    DvmAudioDeviceMonitor *monitor = user_data;
    if (sequence == monitor->initial_sync_sequence)
        monitor->initialized = 1;
}

static const struct pw_registry_events device_registry_events = {
    PW_VERSION_REGISTRY_EVENTS,
    .global = on_device_registry_global,
    .global_remove = on_device_registry_global_remove
};

static const struct pw_core_events device_core_events = {
    PW_VERSION_CORE_EVENTS,
    .done = on_device_core_done,
    .error = on_device_core_error
};

static int refresh_device_snapshot(void)
{
    initialize_pipewire();
    DvmAudioDeviceSnapshot refreshed = {0};
    struct pw_main_loop *loop = pw_main_loop_new(NULL);
    if (loop == NULL)
        return DVM_AUDIO_ALLOCATION_FAILED;

    struct pw_context *context = pw_context_new(pw_main_loop_get_loop(loop), NULL, 0);
    if (context == NULL) {
        pw_main_loop_destroy(loop);
        return DVM_AUDIO_ALLOCATION_FAILED;
    }
    struct pw_core *core = pw_context_connect(context, NULL, 0);
    if (core == NULL) {
        pw_context_destroy(context);
        pw_main_loop_destroy(loop);
        return DVM_AUDIO_PIPEWIRE_FAILED;
    }
    struct pw_registry *registry = pw_core_get_registry(
        core,
        PW_VERSION_REGISTRY,
        0);
    if (registry == NULL) {
        pw_core_disconnect(core);
        pw_context_destroy(context);
        pw_main_loop_destroy(loop);
        return DVM_AUDIO_PIPEWIRE_FAILED;
    }

    DvmAudioDiscovery discovery = {
        .loop = loop,
        .snapshot = &refreshed
    };
    struct spa_hook registry_listener = {0};
    struct spa_hook core_listener = {0};
    pw_registry_add_listener(
        registry,
        &registry_listener,
        &registry_events,
        &discovery);
    pw_core_add_listener(core, &core_listener, &core_events, &discovery);
    discovery.sync_sequence = pw_core_sync(core, PW_ID_CORE, 0);
    if (discovery.sync_sequence < 0)
        discovery.failed = 1;
    else
        pw_main_loop_run(loop);

    spa_hook_remove(&registry_listener);
    spa_hook_remove(&core_listener);
    pw_proxy_destroy((struct pw_proxy *)registry);
    pw_core_disconnect(core);
    pw_context_destroy(context);
    pw_main_loop_destroy(loop);
    if (discovery.failed)
        return DVM_AUDIO_PIPEWIRE_FAILED;

    device_snapshot = refreshed;
    return DVM_AUDIO_OK;
}

static void on_stream_state_changed(
    void *user_data,
    enum pw_stream_state old_state,
    enum pw_stream_state state,
    const char *error)
{
    (void)old_state;
    (void)error;
    DvmAudioStream *stream = user_data;
    atomic_store_explicit(&stream->state, (int32_t)state, memory_order_release);
}

static void process_capture(DvmAudioStream *stream, struct pw_buffer *pipewire_buffer)
{
    struct spa_buffer *buffer = pipewire_buffer->buffer;
    if (buffer->n_datas == 0)
        return;

    struct spa_data *data = &buffer->datas[0];
    if (data->data == NULL || data->chunk == NULL)
        return;

    uint32_t sample_bytes = sizeof(int16_t);
    uint32_t offset = data->chunk->offset;
    uint32_t size = data->chunk->size;
    if (offset > data->maxsize || size > data->maxsize - offset)
        return;

    const int16_t *samples = (const int16_t *)((const uint8_t *)data->data + offset);
    if (dvm_pcm_ring_push(&stream->ring, samples, size / sample_bytes) > 0)
        dvm_capture_signal_notify(&stream->capture_signal);
}

static void process_playback(DvmAudioStream *stream, struct pw_buffer *pipewire_buffer)
{
    struct spa_buffer *buffer = pipewire_buffer->buffer;
    if (buffer->n_datas == 0)
        return;

    struct spa_data *data = &buffer->datas[0];
    if (data->data == NULL || data->chunk == NULL)
        return;

    uint32_t frame_bytes = (uint32_t)stream->channels * sizeof(int16_t);
    uint32_t frame_count = pipewire_buffer->requested;
    uint32_t maximum_frames = frame_bytes == 0 ? 0 : data->maxsize / frame_bytes;
    if (frame_count == 0 || frame_count > maximum_frames)
        frame_count = maximum_frames;

    uint32_t requested_samples = frame_count * (uint32_t)stream->channels;
    int16_t *samples = data->data;
    uint32_t copied = dvm_pcm_ring_pop(&stream->ring, samples, requested_samples);
    if (copied < requested_samples)
        memset(samples + copied, 0, (requested_samples - copied) * sizeof(int16_t));

    if (copied > 0) {
        uint64_t pending = atomic_exchange_explicit(
            &stream->pending_starved_samples,
            0,
            memory_order_acq_rel);
        atomic_fetch_add_explicit(&stream->starved_samples, pending, memory_order_relaxed);
    }
    if (atomic_load_explicit(&stream->continuity_active, memory_order_acquire) != 0 &&
        copied < requested_samples) {
        atomic_fetch_add_explicit(
            &stream->pending_starved_samples,
            requested_samples - copied,
            memory_order_relaxed);
    }

    atomic_fetch_add_explicit(&stream->output_callback_count, 1, memory_order_relaxed);
    data->chunk->offset = 0;
    data->chunk->stride = (int32_t)frame_bytes;
    data->chunk->size = requested_samples * sizeof(int16_t);
}

static void on_stream_process(void *user_data)
{
    DvmAudioStream *stream = user_data;
    struct pw_buffer *buffer = pw_stream_dequeue_buffer(stream->stream);
    if (buffer == NULL)
        return;

    if (stream->input != 0)
        process_capture(stream, buffer);
    else
        process_playback(stream, buffer);
    pw_stream_queue_buffer(stream->stream, buffer);
}

static const struct pw_stream_events stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .state_changed = on_stream_state_changed,
    .process = on_stream_process
};

DVM_EXPORT int32_t dvm_audio_request_microphone_permission(void)
{
    return DVM_PERMISSION_UNAVAILABLE;
}

DVM_EXPORT DvmAudioDeviceMonitor *dvm_audio_device_monitor_create(
    DvmAudioDeviceChangedCallback callback,
    void *user_data)
{
    if (callback == NULL)
        return NULL;
    initialize_pipewire();

    DvmAudioDeviceMonitor *monitor = calloc(1, sizeof(DvmAudioDeviceMonitor));
    if (monitor == NULL)
        return NULL;
    monitor->callback = callback;
    monitor->user_data = user_data;
    monitor->loop = pw_thread_loop_new("DVM Console device monitor", NULL);
    if (monitor->loop == NULL)
        goto fail;
    monitor->context = pw_context_new(pw_thread_loop_get_loop(monitor->loop), NULL, 0);
    if (monitor->context == NULL)
        goto fail;
    monitor->core = pw_context_connect(monitor->context, NULL, 0);
    if (monitor->core == NULL)
        goto fail;
    monitor->registry = pw_core_get_registry(monitor->core, PW_VERSION_REGISTRY, 0);
    if (monitor->registry == NULL)
        goto fail;
    pw_core_add_listener(
        monitor->core,
        &monitor->core_listener,
        &device_core_events,
        monitor);
    pw_registry_add_listener(
        monitor->registry,
        &monitor->registry_listener,
        &device_registry_events,
        monitor);
    monitor->initial_sync_sequence = pw_core_sync(monitor->core, PW_ID_CORE, 0);
    if (monitor->initial_sync_sequence < 0)
        goto fail;
    if (pw_thread_loop_start(monitor->loop) < 0)
        goto fail;
    return monitor;

fail:
    if (monitor->metadata != NULL)
        pw_proxy_destroy((struct pw_proxy *)monitor->metadata);
    if (monitor->registry != NULL)
        pw_proxy_destroy((struct pw_proxy *)monitor->registry);
    if (monitor->core != NULL)
        pw_core_disconnect(monitor->core);
    if (monitor->context != NULL)
        pw_context_destroy(monitor->context);
    if (monitor->loop != NULL)
        pw_thread_loop_destroy(monitor->loop);
    free(monitor);
    return NULL;
}

DVM_EXPORT void dvm_audio_device_monitor_destroy(DvmAudioDeviceMonitor *monitor)
{
    if (monitor == NULL)
        return;
    if (monitor->loop != NULL)
        pw_thread_loop_stop(monitor->loop);
    if (monitor->metadata != NULL) {
        spa_hook_remove(&monitor->metadata_listener);
        pw_proxy_destroy((struct pw_proxy *)monitor->metadata);
    }
    if (monitor->registry != NULL) {
        spa_hook_remove(&monitor->registry_listener);
        pw_proxy_destroy((struct pw_proxy *)monitor->registry);
    }
    if (monitor->core != NULL) {
        spa_hook_remove(&monitor->core_listener);
        pw_core_disconnect(monitor->core);
    }
    if (monitor->context != NULL)
        pw_context_destroy(monitor->context);
    if (monitor->loop != NULL)
        pw_thread_loop_destroy(monitor->loop);
    free(monitor);
}

DVM_EXPORT int32_t dvm_audio_get_device_count(int32_t input, int32_t *count)
{
    if ((input != 0 && input != 1) || count == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;
    pthread_mutex_lock(&device_snapshot_mutex);
    int32_t result = refresh_device_snapshot();
    if (result == DVM_AUDIO_OK) {
        int32_t physical_count = input != 0
            ? device_snapshot.input_count
            : device_snapshot.output_count;
        *count = physical_count + 1;
    }
    pthread_mutex_unlock(&device_snapshot_mutex);
    return result;
}

DVM_EXPORT int32_t dvm_audio_get_device(
    int32_t input,
    int32_t index,
    uint64_t *device_id,
    char *name,
    uint32_t name_capacity,
    int32_t *is_default)
{
    if ((input != 0 && input != 1) || index < 0 || device_id == NULL ||
        name == NULL || name_capacity == 0 || is_default == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;

    pthread_mutex_lock(&device_snapshot_mutex);
    DvmAudioDevice *devices = input != 0
        ? device_snapshot.inputs
        : device_snapshot.outputs;
    int32_t count = input != 0
        ? device_snapshot.input_count
        : device_snapshot.output_count;
    if (index > count) {
        pthread_mutex_unlock(&device_snapshot_mutex);
        return DVM_AUDIO_NOT_FOUND;
    }

    const char *display_name;
    if (index == 0) {
        display_name = input != 0
            ? "PipeWire default input"
            : "PipeWire default output";
        *device_id = 0;
        *is_default = 1;
    }
    else {
        DvmAudioDevice *device = &devices[index - 1];
        display_name = device->name;
        *device_id = device->id;
        *is_default = 0;
    }
    size_t length = strlen(display_name);
    if (length >= name_capacity)
        length = name_capacity - 1;
    memcpy(name, display_name, length);
    name[length] = '\0';
    pthread_mutex_unlock(&device_snapshot_mutex);
    return DVM_AUDIO_OK;
}

DVM_EXPORT int32_t dvm_audio_device_is_bluetooth(uint64_t device_id)
{
    if (device_id == 0)
        return -1;
    pthread_mutex_lock(&device_snapshot_mutex);
    for (int32_t index = 0; index < device_snapshot.input_count; index++) {
        if (device_snapshot.inputs[index].id == device_id) {
            int result = device_snapshot.inputs[index].bluetooth;
            pthread_mutex_unlock(&device_snapshot_mutex);
            return result;
        }
    }
    for (int32_t index = 0; index < device_snapshot.output_count; index++) {
        if (device_snapshot.outputs[index].id == device_id) {
            int result = device_snapshot.outputs[index].bluetooth;
            pthread_mutex_unlock(&device_snapshot_mutex);
            return result;
        }
    }
    pthread_mutex_unlock(&device_snapshot_mutex);
    return -1;
}

DVM_EXPORT DvmAudioStream *dvm_audio_stream_create(
    uint64_t device_id,
    int32_t input,
    int32_t sample_rate,
    int32_t channels,
    int32_t bits_per_sample)
{
    if ((input != 0 && input != 1) || sample_rate <= 0 ||
        (channels != 1 && channels != 2) || bits_per_sample != 16)
        return NULL;

    initialize_pipewire();
    DvmAudioStream *stream = calloc(1, sizeof(*stream));
    if (stream == NULL)
        return NULL;

    stream->input = input;
    stream->sample_rate = sample_rate;
    stream->channels = channels;
    atomic_init(&stream->state, PW_STREAM_STATE_UNCONNECTED);
    atomic_init(&stream->continuity_active, 0);
    atomic_init(&stream->starved_samples, 0);
    atomic_init(&stream->pending_starved_samples, 0);
    atomic_init(&stream->output_callback_count, 0);
    uint32_t ring_capacity = (uint32_t)sample_rate * (uint32_t)channels * 2;
    if (dvm_pcm_ring_init(&stream->ring, ring_capacity) != 0) {
        free(stream);
        return NULL;
    }
    if (stream->input != 0 && dvm_capture_signal_init(&stream->capture_signal) != 0)
        goto fail;

    stream->loop = pw_thread_loop_new("DVM Console PipeWire", NULL);
    if (stream->loop == NULL)
        goto fail;
    if (pw_thread_loop_start(stream->loop) < 0)
        goto fail;

    pw_thread_loop_lock(stream->loop);
    struct pw_properties *properties = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Audio",
        PW_KEY_MEDIA_CATEGORY, input != 0 ? "Capture" : "Playback",
        PW_KEY_MEDIA_ROLE, "Communication",
        PW_KEY_APP_NAME, "DVM Console NEO",
        PW_KEY_NODE_NAME, input != 0 ? "dvmconsole.capture" : "dvmconsole.playback",
        NULL);
    if (properties == NULL) {
        pw_thread_loop_unlock(stream->loop);
        goto fail_started;
    }
    if (device_id != 0) {
        char target_serial[32];
        snprintf(target_serial, sizeof(target_serial), "%" PRIu64, device_id);
        if (pw_properties_set(properties, PW_KEY_TARGET_OBJECT, target_serial) < 0) {
            pw_properties_free(properties);
            pw_thread_loop_unlock(stream->loop);
            goto fail_started;
        }
    }
    stream->stream = pw_stream_new_simple(
        pw_thread_loop_get_loop(stream->loop),
        input != 0 ? "DVM Console capture" : "DVM Console playback",
        properties,
        &stream_events,
        stream);
    if (stream->stream == NULL) {
        pw_thread_loop_unlock(stream->loop);
        goto fail_started;
    }

    uint8_t parameter_buffer[1024];
    struct spa_pod_builder builder = SPA_POD_BUILDER_INIT(
        parameter_buffer,
        sizeof(parameter_buffer));
    struct spa_audio_info_raw audio_info = SPA_AUDIO_INFO_RAW_INIT(
        .format = SPA_AUDIO_FORMAT_S16_LE,
        .rate = (uint32_t)sample_rate,
        .channels = (uint32_t)channels);
    if (channels == 1)
        audio_info.position[0] = SPA_AUDIO_CHANNEL_MONO;
    else {
        audio_info.position[0] = SPA_AUDIO_CHANNEL_FL;
        audio_info.position[1] = SPA_AUDIO_CHANNEL_FR;
    }
    const struct spa_pod *parameters[1] = {
        spa_format_audio_raw_build(&builder, SPA_PARAM_EnumFormat, &audio_info)
    };
    enum pw_direction direction = input != 0
        ? PW_DIRECTION_INPUT
        : PW_DIRECTION_OUTPUT;
    enum pw_stream_flags flags = PW_STREAM_FLAG_AUTOCONNECT |
        PW_STREAM_FLAG_MAP_BUFFERS |
        PW_STREAM_FLAG_RT_PROCESS |
        PW_STREAM_FLAG_INACTIVE;
    int connect_result = pw_stream_connect(
        stream->stream,
        direction,
        PW_ID_ANY,
        flags,
        parameters,
        1);
    pw_thread_loop_unlock(stream->loop);
    if (connect_result < 0)
        goto fail_stream;

    return stream;

fail_stream:
    pw_thread_loop_lock(stream->loop);
    pw_stream_destroy(stream->stream);
    stream->stream = NULL;
    pw_thread_loop_unlock(stream->loop);
fail_started:
    pw_thread_loop_stop(stream->loop);
fail:
    if (stream->loop != NULL)
        pw_thread_loop_destroy(stream->loop);
    dvm_pcm_ring_dispose(&stream->ring);
    if (stream->input != 0)
        dvm_capture_signal_dispose(&stream->capture_signal);
    free(stream);
    return NULL;
}

DVM_EXPORT int32_t dvm_audio_stream_start(DvmAudioStream *stream)
{
    if (stream == NULL || stream->stream == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;
    if (atomic_load_explicit(&stream->state, memory_order_acquire) == PW_STREAM_STATE_ERROR)
        return DVM_AUDIO_STREAM_FAILED;
    pw_thread_loop_lock(stream->loop);
    int result = pw_stream_set_active(stream->stream, true);
    pw_thread_loop_unlock(stream->loop);
    return result < 0 ? DVM_AUDIO_PIPEWIRE_FAILED : DVM_AUDIO_OK;
}

DVM_EXPORT int32_t dvm_audio_stream_stop(DvmAudioStream *stream)
{
    if (stream == NULL || stream->stream == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;
    pw_thread_loop_lock(stream->loop);
    int result = pw_stream_set_active(stream->stream, false);
    pw_thread_loop_unlock(stream->loop);
    if (stream->input == 0) {
        atomic_store_explicit(&stream->continuity_active, 0, memory_order_release);
        atomic_store_explicit(&stream->pending_starved_samples, 0, memory_order_release);
    }
    else {
        dvm_capture_signal_notify(&stream->capture_signal);
    }
    return result < 0 ? DVM_AUDIO_PIPEWIRE_FAILED : DVM_AUDIO_OK;
}

DVM_EXPORT int32_t dvm_audio_stream_get_sample_rate(DvmAudioStream *stream)
{
    return stream == NULL ? DVM_AUDIO_INVALID_ARGUMENT : stream->sample_rate;
}

DVM_EXPORT int32_t dvm_audio_stream_read(
    DvmAudioStream *stream,
    int16_t *samples,
    uint32_t capacity)
{
    if (stream == NULL || stream->input == 0 || samples == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;
    return (int32_t)dvm_pcm_ring_pop(&stream->ring, samples, capacity);
}

DVM_EXPORT int32_t dvm_audio_stream_wait_for_capture(
    DvmAudioStream *stream,
    int32_t timeout_ms)
{
    if (stream == NULL || stream->input == 0)
        return DVM_AUDIO_INVALID_ARGUMENT;
    if (dvm_pcm_ring_count(&stream->ring) > 0)
        return 1;
    int result = dvm_capture_signal_wait(&stream->capture_signal, timeout_ms);
    if (result <= 0)
        return result;
    return dvm_pcm_ring_count(&stream->ring) > 0 ? 1 : 0;
}

DVM_EXPORT void dvm_audio_stream_wake_capture(DvmAudioStream *stream)
{
    if (stream != NULL && stream->input != 0)
        dvm_capture_signal_notify(&stream->capture_signal);
}

DVM_EXPORT int32_t dvm_audio_stream_write(
    DvmAudioStream *stream,
    const int16_t *samples,
    uint32_t count)
{
    if (stream == NULL || stream->input != 0 || samples == NULL)
        return DVM_AUDIO_INVALID_ARGUMENT;
    uint32_t accepted = dvm_pcm_ring_push(&stream->ring, samples, count);
    if (accepted > 0)
        atomic_store_explicit(&stream->continuity_active, 1, memory_order_release);
    return (int32_t)accepted;
}

DVM_EXPORT uint32_t dvm_audio_stream_queued_samples(DvmAudioStream *stream)
{
    return stream == NULL ? 0 : dvm_pcm_ring_count(&stream->ring);
}

DVM_EXPORT uint64_t dvm_audio_stream_starved_samples(DvmAudioStream *stream)
{
    return stream == NULL ? 0 :
        atomic_load_explicit(&stream->starved_samples, memory_order_acquire);
}

DVM_EXPORT uint64_t dvm_audio_stream_pending_starved_samples(DvmAudioStream *stream)
{
    return stream == NULL ? 0 :
        atomic_load_explicit(&stream->pending_starved_samples, memory_order_acquire);
}

DVM_EXPORT uint64_t dvm_audio_stream_output_callback_count(DvmAudioStream *stream)
{
    return stream == NULL ? 0 :
        atomic_load_explicit(&stream->output_callback_count, memory_order_acquire);
}

DVM_EXPORT uint64_t dvm_audio_stream_output_presentation_latency_ns(DvmAudioStream *stream)
{
    (void)stream;
    return 0;
}

DVM_EXPORT void dvm_audio_stream_end_playback_continuity(DvmAudioStream *stream)
{
    if (stream == NULL)
        return;
    atomic_store_explicit(&stream->continuity_active, 0, memory_order_release);
    atomic_store_explicit(&stream->pending_starved_samples, 0, memory_order_release);
}

DVM_EXPORT void dvm_audio_stream_destroy(DvmAudioStream *stream)
{
    if (stream == NULL)
        return;
    if (stream->stream != NULL && stream->loop != NULL) {
        pw_thread_loop_lock(stream->loop);
        pw_stream_destroy(stream->stream);
        stream->stream = NULL;
        pw_thread_loop_unlock(stream->loop);
    }
    if (stream->loop != NULL) {
        pw_thread_loop_stop(stream->loop);
        pw_thread_loop_destroy(stream->loop);
    }
    dvm_pcm_ring_dispose(&stream->ring);
    if (stream->input != 0)
        dvm_capture_signal_dispose(&stream->capture_signal);
    free(stream);
}
