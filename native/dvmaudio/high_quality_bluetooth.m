#import "dvmaudio.h"

#import <AVFAudio/AVFAudio.h>
#import <CoreAudio/CoreAudio.h>
#import <Foundation/Foundation.h>
#import <dlfcn.h>
#import <objc/message.h>
#import <objc/runtime.h>

static const NSUInteger dvmMixWithOthers = 1UL << 0;
static const NSUInteger dvmAllowBluetoothHFP = 1UL << 2;
static const NSUInteger dvmBluetoothHighQualityRecording = 1UL << 19;

static id dvmAudioSession;
static AVAudioEngine *dvmKeepAliveEngine;
static AudioDeviceID dvmInputDevice = kAudioObjectUnknown;
static AudioDeviceID dvmOutputDevice = kAudioObjectUnknown;
static NSUInteger dvmSessionReferences;
static int32_t dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_OFF;

static id dvmSymbolObject(const char *name)
{
    id __unsafe_unretained *address =
        (id __unsafe_unretained *)dlsym(RTLD_DEFAULT, name);
    return address ? *address : nil;
}

static id dvmSendObject(id object, const char *selector_name)
{
    if (object == nil)
        return nil;
    SEL selector = sel_registerName(selector_name);
    if (![object respondsToSelector:selector])
        return nil;
    id (*send)(id, SEL) = (id (*)(id, SEL))objc_msgSend;
    return send(object, selector);
}

static BOOL dvmSendBool(id object, const char *selector_name, BOOL *available)
{
    if (available != NULL)
        *available = NO;
    if (object == nil)
        return NO;
    SEL selector = sel_registerName(selector_name);
    if (![object respondsToSelector:selector])
        return NO;
    if (available != NULL)
        *available = YES;
    BOOL (*send)(id, SEL) = (BOOL (*)(id, SEL))objc_msgSend;
    return send(object, selector);
}

static AudioDeviceID dvmDefaultDevice(BOOL input)
{
    AudioDeviceID device = kAudioObjectUnknown;
    UInt32 size = sizeof(device);
    AudioObjectPropertyAddress address = {
        input ? kAudioHardwarePropertyDefaultInputDevice
              : kAudioHardwarePropertyDefaultOutputDevice,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL,
                                   &size, &device) != noErr)
        return kAudioObjectUnknown;
    return device;
}

static double dvmDeviceRate(AudioDeviceID device)
{
    Float64 rate = 0;
    UInt32 size = sizeof(rate);
    AudioObjectPropertyAddress address = {
        kAudioDevicePropertyNominalSampleRate,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    if (AudioObjectGetPropertyData(device, &address, 0, NULL, &size, &rate) != noErr)
        return 0;
    return rate;
}

static id dvmHighQualityCapability(id session)
{
    id route = dvmSendObject(session, "currentRoute");
    NSArray *inputs = dvmSendObject(route, "inputs");
    id input = inputs.firstObject;
    id extension = dvmSendObject(input, "bluetoothMicrophoneExtension");
    return dvmSendObject(extension, "highQualityRecording");
}

static void dvmDeactivateSession(void)
{
    if (dvmKeepAliveEngine != nil) {
        AVAudioInputNode *input = dvmKeepAliveEngine.inputNode;
        [dvmKeepAliveEngine stop];
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [input removeTapOnBus:0];
#pragma clang diagnostic pop
        dvmKeepAliveEngine = nil;
    }
    if (dvmAudioSession != nil) {
        SEL selector = sel_registerName("setActive:withOptions:error:");
        if ([dvmAudioSession respondsToSelector:selector]) {
            BOOL (*set_inactive)(id, SEL, BOOL, NSUInteger, NSError **) =
                (BOOL (*)(id, SEL, BOOL, NSUInteger, NSError **))objc_msgSend;
            NSError *error = nil;
            set_inactive(dvmAudioSession, selector, NO, 1, &error);
        }
    }
    dvmAudioSession = nil;
    dvmInputDevice = kAudioObjectUnknown;
    dvmOutputDevice = kAudioObjectUnknown;
    dvmSessionReferences = 0;
}

int32_t dvm_audio_high_quality_bluetooth_acquire(
    uint64_t input_device_id,
    uint64_t output_device_id)
{
    @synchronized([NSProcessInfo class]) {
        NSOperatingSystemVersion minimum = {26, 0, 0};
        if (![NSProcessInfo.processInfo isOperatingSystemAtLeastVersion:minimum]) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_OFF;
            return 0;
        }

        AudioDeviceID input = (AudioDeviceID)input_device_id;
        AudioDeviceID output = (AudioDeviceID)output_device_id;
        if (input == kAudioObjectUnknown || output == kAudioObjectUnknown ||
            input != dvmDefaultDevice(YES) || output != dvmDefaultDevice(NO) ||
            dvm_audio_device_is_bluetooth(input) != 1 ||
            dvm_audio_device_is_bluetooth(output) != 1) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_OFF;
            return 0;
        }

        if (dvmSessionReferences > 0) {
            if (input != dvmInputDevice || output != dvmOutputDevice)
                return 0;
            dvmSessionReferences++;
            return 1;
        }

        Class session_class = NSClassFromString(@"AVAudioSession");
        SEL shared_selector = sel_registerName("sharedInstance");
        if (session_class == Nil || ![session_class respondsToSelector:shared_selector]) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE;
            return 0;
        }

        id (*shared_instance)(id, SEL) = (id (*)(id, SEL))objc_msgSend;
        id session = shared_instance(session_class, shared_selector);
        id category = dvmSymbolObject("AVAudioSessionCategoryPlayAndRecord");
        id mode = dvmSymbolObject("AVAudioSessionModeDefault");
        SEL category_selector = sel_registerName("setCategory:mode:options:error:");
        SEL active_selector = sel_registerName("setActive:error:");
        if (session == nil || category == nil || mode == nil ||
            ![session respondsToSelector:category_selector] ||
            ![session respondsToSelector:active_selector]) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE;
            return 0;
        }

        NSError *error = nil;
        BOOL (*set_category)(id, SEL, id, id, NSUInteger, NSError **) =
            (BOOL (*)(id, SEL, id, id, NSUInteger, NSError **))objc_msgSend;
        NSUInteger options = dvmMixWithOthers | dvmAllowBluetoothHFP |
                             dvmBluetoothHighQualityRecording;
        if (!set_category(session, category_selector, category, mode, options, &error)) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE;
            return 0;
        }

        SEL rate_selector = sel_registerName("setPreferredSampleRate:error:");
        if ([session respondsToSelector:rate_selector]) {
            BOOL (*set_rate)(id, SEL, double, NSError **) =
                (BOOL (*)(id, SEL, double, NSError **))objc_msgSend;
            error = nil;
            set_rate(session, rate_selector, 48000.0, &error);
        }

        BOOL (*set_active)(id, SEL, BOOL, NSError **) =
            (BOOL (*)(id, SEL, BOOL, NSError **))objc_msgSend;
        error = nil;
        if (!set_active(session, active_selector, YES, &error)) {
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE;
            return 0;
        }

        id capability = dvmHighQualityCapability(session);
        BOOL support_available = NO;
        BOOL supported = dvmSendBool(capability, "isSupported", &support_available);
        if (support_available && !supported) {
            dvmAudioSession = session;
            dvmDeactivateSession();
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNSUPPORTED;
            return 0;
        }

        dvmAudioSession = session;
        dvmInputDevice = input;
        dvmOutputDevice = output;
        dvmKeepAliveEngine = [AVAudioEngine new];
        AVAudioInputNode *input_node = dvmKeepAliveEngine.inputNode;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [input_node installTapOnBus:0
                        bufferSize:1024
                            format:nil
                             block:^(__unused AVAudioPCMBuffer *buffer,
                                     __unused AVAudioTime *when) {
                             }];
#pragma clang diagnostic pop
        [dvmKeepAliveEngine prepare];
        error = nil;
        if (![dvmKeepAliveEngine startAndReturnError:&error]) {
            dvmDeactivateSession();
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNAVAILABLE;
            return 0;
        }

        capability = dvmHighQualityCapability(session);
        support_available = NO;
        BOOL enabled_available = NO;
        supported = dvmSendBool(capability, "isSupported", &support_available);
        BOOL enabled = dvmSendBool(capability, "isEnabled", &enabled_available);
        BOOL full_bandwidth = dvmDeviceRate(input) >= 44100.0 &&
                              dvmDeviceRate(output) >= 44100.0;
        if ((support_available && !supported) ||
            (!support_available && !full_bandwidth)) {
            dvmDeactivateSession();
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNSUPPORTED;
            return 0;
        }

        dvmSessionReferences = 1;
        dvmSessionStatus = (enabled_available && enabled) || full_bandwidth
            ? DVM_HIGH_QUALITY_BLUETOOTH_ACTIVE
            : DVM_HIGH_QUALITY_BLUETOOTH_REQUESTED;
        return 1;
    }
}

void dvm_audio_high_quality_bluetooth_release(void)
{
    @synchronized([NSProcessInfo class]) {
        if (dvmSessionReferences == 0)
            return;
        if (--dvmSessionReferences > 0)
            return;
        dvmDeactivateSession();
        dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_OFF;
    }
}

int32_t dvm_audio_high_quality_bluetooth_status(void)
{
    @synchronized([NSProcessInfo class]) {
        if (dvmSessionReferences == 0 || dvmAudioSession == nil)
            return dvmSessionStatus;

        id capability = dvmHighQualityCapability(dvmAudioSession);
        BOOL support_available = NO;
        BOOL enabled_available = NO;
        BOOL supported = dvmSendBool(capability, "isSupported", &support_available);
        BOOL enabled = dvmSendBool(capability, "isEnabled", &enabled_available);
        if (support_available && !supported)
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_UNSUPPORTED;
        else if ((enabled_available && enabled) ||
                 (dvmDeviceRate(dvmInputDevice) >= 44100.0 &&
                  dvmDeviceRate(dvmOutputDevice) >= 44100.0))
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_ACTIVE;
        else
            dvmSessionStatus = DVM_HIGH_QUALITY_BLUETOOTH_REQUESTED;
        return dvmSessionStatus;
    }
}
