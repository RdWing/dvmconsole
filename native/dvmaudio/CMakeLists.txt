cmake_minimum_required(VERSION 3.20)

project(dvmaudio LANGUAGES C OBJC)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(dvmaudio SHARED
    dvm_pcm_ring.c
    dvmaudio.c
    privacy_permissions.m)
set_source_files_properties(privacy_permissions.m PROPERTIES
    COMPILE_OPTIONS "-fobjc-arc;-fblocks")
target_compile_options(dvmaudio PRIVATE -Wall -Wextra -Wpedantic)
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
    add_executable(dvm_pcm_ring_tests
        dvm_pcm_ring.c
        dvm_pcm_ring_tests.c)
    target_compile_options(dvm_pcm_ring_tests PRIVATE -Wall -Wextra -Wpedantic)
    add_test(NAME dvm_pcm_ring_tests COMMAND dvm_pcm_ring_tests)
endif()
