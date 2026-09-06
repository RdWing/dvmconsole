cmake_minimum_required(VERSION 3.20)

file(STRINGS
    "${CMAKE_CURRENT_SOURCE_DIR}/../../packaging/macos/deployment-target.txt"
    DVMCONSOLE_MACOS_DEPLOYMENT_TARGET
    LIMIT_COUNT 1)
if(NOT CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET
        "${DVMCONSOLE_MACOS_DEPLOYMENT_TARGET}"
        CACHE STRING "Minimum supported macOS version")
endif()

project(dvmaudio LANGUAGES C OBJC)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(dvmaudio SHARED
    dvm_capture_signal.c
    dvm_pcm_ring.c
    dvmaudio.c
    privacy_permissions.m)
set_source_files_properties(privacy_permissions.m PROPERTIES
    COMPILE_OPTIONS "-fobjc-arc;-fblocks")
target_compile_options(dvmaudio PRIVATE
    -Wall
    -Wextra
    -Wpedantic
    -Werror=unguarded-availability-new)
target_link_libraries(dvmaudio PRIVATE
    "-framework AudioToolbox"
    "-framework AudioUnit"
    "-framework AVFoundation"
    "-framework CoreAudio"
    "-framework CoreFoundation"
    "-framework Foundation")
set_target_properties(dvmaudio PROPERTIES OUTPUT_NAME "dvmaudio")

include(CTest)
if(BUILD_TESTING)
    find_package(Threads REQUIRED)
    add_executable(dvm_capture_signal_tests
        dvm_capture_signal.c
        dvm_capture_signal_tests.c)
    target_compile_options(dvm_capture_signal_tests PRIVATE -Wall -Wextra -Wpedantic)
    target_link_libraries(dvm_capture_signal_tests PRIVATE Threads::Threads)
    add_test(NAME dvm_capture_signal_tests COMMAND dvm_capture_signal_tests)

    add_executable(dvm_pcm_ring_tests
        dvm_pcm_ring.c
        dvm_pcm_ring_tests.c)
    target_compile_options(dvm_pcm_ring_tests PRIVATE -Wall -Wextra -Wpedantic)
    add_test(NAME dvm_pcm_ring_tests COMMAND dvm_pcm_ring_tests)
endif()
