#import <AVFoundation/AVFoundation.h>

#import "dvmaudio.h"

int32_t dvm_audio_request_microphone_permission(void)
{
    @autoreleasepool {
        switch ([AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio]) {
            case AVAuthorizationStatusAuthorized:
                return DVM_PERMISSION_GRANTED;
            case AVAuthorizationStatusDenied:
                return DVM_PERMISSION_DENIED;
            case AVAuthorizationStatusRestricted:
                return DVM_PERMISSION_RESTRICTED;
            case AVAuthorizationStatusNotDetermined:
                [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio
                                         completionHandler:^(__unused BOOL granted) {
                                         }];
                return DVM_PERMISSION_REQUESTED;
        }
    }

    return DVM_PERMISSION_UNAVAILABLE;
}
